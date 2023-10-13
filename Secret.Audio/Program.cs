using System.Numerics;
using MathNet.Numerics.IntegralTransforms;
using NAudio.CoreAudioApi;
using NAudio.Wave;

Enum.TryParse<Mode>("Dual", true, out var mode);
var randomizePackets = bool.Parse(bool.TrueString);

const int samplesRate = 192000;
//in unreliable transfer this can be increase to repeat more same signal for better decoding
//should be equal or more than 4, otherwise frequency value can't be reliably detected as there are not enough samples
const int samplesPerSignal = 16 * 4;

//window used during decoding phase
//should be equal or less than samplesPerSignal
const int segmentLength = samplesPerSignal;
//should be equal or less than segmentLength
//by default set it to 50% overlap
const int segmentOverlap = samplesPerSignal * 2 / 4;

const float amplitude = 0.75f;

//we choose frequency of bit separator signal in between positive and negative sequence
//this should eliminate possibility of ambiguity when jumping between negative, separator and positive signals
const int negativeSignal = 4000;
const int positiveSignal = 12000;
const int bitSignal = 8000;
const int packetSignal = 16000;

const int negative = 0;
const int positive = 1;
const int bitSeparator = 2;
const int packetSeparator = 3;

const int audioHeaderPreambleSize = 8 * 1024;
const int audioFooterPreambleSize = 8 * 1024;
const int audioPacketPreambleSize = 8 * 128;
const int audioPacketPreambleDetection = 8;

const int packetHeaderSize = 4 * sizeof(short);
const int packetSize = 1 * 1024;

const int playCount = 1;

const string sourceDataPath = @"C:\TEMP\file.txt";
const string sourceWavPath = @"C:\TEMP\file.wav";
const string outputWavPath = @"C:\TEMP\file.out.wav";
const string outputDataPath = @"C:\TEMP\file.out.txt";

var format = new WaveFormat(samplesRate, 1);
var recordTime = DateTime.Now;

var capture = new WasapiLoopbackCapture(new MMDeviceEnumerator().EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).First())
{
    WaveFormat = format
};

if (mode is Mode.Dual or Mode.Playing)
{
    if (File.Exists(sourceWavPath))
        File.Delete(sourceWavPath);
    Console.WriteLine("Create audio");
    await BytesToWav();
    Console.WriteLine("Created audio");
}

switch (mode)
{
    case Mode.Playing:
        Console.WriteLine("Press key to start playing");
        Console.ReadKey();
        Console.WriteLine("Started");
        await PlayWav(null);
        Console.WriteLine("Played");
        break;
    case Mode.Recording:
        if (File.Exists(outputWavPath))
            File.Delete(outputWavPath);

        Console.WriteLine("Press key to start recording");
        Console.ReadKey();
        Console.WriteLine("Started");
        recordTime = DateTime.Now;
        var recordStopTask = StopRecording(capture, 15);
        var recordTask = RecordWav(capture);
        await recordStopTask;
        Console.WriteLine("Stopped recording");
        await recordTask;
        Console.WriteLine("Recorded");
        break;
    case Mode.Dual:
        if (File.Exists(outputWavPath))
            File.Delete(outputWavPath);

        await RecordBytes(capture, PlayWav);
        break;
}

if (mode is Mode.Dual or Mode.Recording)
{
    if (File.Exists(outputDataPath))
        File.Delete(outputDataPath);
}

if (mode is Mode.Dual or Mode.Recording or Mode.Decoding)
{    
    Console.WriteLine("Start decoding");
    var packetsCount = await WavToBytes();
    if (packetsCount >= 0)
    {
        Console.WriteLine("Completed decoding, expected number of packets {0}", packetsCount);
        await TryCombineDecodedPackets(packetsCount);
    }
    else
    {
        Console.WriteLine("Was unable to decode");
    }
}

Console.ReadKey();

async Task PlayWav(IWaveIn? waveIn)
{
    var lastPercent = 0d;

    var wavInStream = new WaveFileReader(sourceWavPath);
    var volumeStream = new WaveChannel32(wavInStream);
    var player = new WaveOutEvent();
    player.Init(volumeStream);
    player.Play();
    while (volumeStream.CurrentTime <= volumeStream.TotalTime)
    {
        ReportPlayProgress(volumeStream.CurrentTime.TotalMilliseconds, volumeStream.TotalTime.TotalMilliseconds);
        await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(100, volumeStream.TotalTime.TotalMilliseconds - volumeStream.CurrentTime.TotalMilliseconds)));
    }
    player.Stop();
    await StopRecording(waveIn, 5);

    void ReportPlayProgress(double progress, double total)
    {
        var percent = 1.0 * progress / total;
        if (percent - lastPercent > 0.1)
        {
            lastPercent = percent;
            Console.WriteLine("Played {0:P}", lastPercent);
        }
    }
}

async Task RecordWav(IWaveIn waveIn)
{
    var stopRecordingTask = new TaskCompletionSource<bool>();
    await using var wavOutStream = new WaveFileWriter(outputWavPath, waveIn.WaveFormat);

    waveIn.DataAvailable += Record;
    waveIn.RecordingStopped += Stop;
    waveIn.StartRecording();

    await stopRecordingTask.Task;

    void Stop(object? sender, StoppedEventArgs args)
    {
        waveIn.DataAvailable -= Record;
        waveIn.RecordingStopped -= Stop;
        waveIn.Dispose();
        stopRecordingTask.TrySetResult(true);
    }

    void Record(object? sender, WaveInEventArgs args)
    {
        if (args.BytesRecorded == 0)
            return;
        wavOutStream.Write(args.Buffer, 0, args.BytesRecorded);
        recordTime = DateTime.Now;
    }
}

async Task StopRecording(IWaveIn? waveIn, int timeoutSeconds)
{
    while (DateTime.Now - recordTime < TimeSpan.FromSeconds(timeoutSeconds))
        await Task.Delay(TimeSpan.FromSeconds(1));
    waveIn?.StopRecording();
}

async Task TryCombineDecodedPackets(int packetsCount)
{
    var packetsDirectoryPath = PreparePacketsDir();
    if (Directory.Exists(packetsDirectoryPath))
    {
        var packetFiles = Directory.GetFiles(packetsDirectoryPath);
        if (packetFiles.Length == packetsCount)
        {
            await using var outputStream = new FileStream(outputDataPath, FileMode.Create);
            foreach (var filePath in packetFiles.OrderBy(f => int.Parse(Path.GetExtension(f)[1..])))
            {
                await using var outputChunkStream = new FileStream(filePath, FileMode.Open);
                await outputChunkStream.CopyToAsync(outputStream);
            }            
            Console.WriteLine("Data received");
        }
        else
        {
            Console.WriteLine("Data has {0} chunks missing", packetsCount - packetFiles.Length);
        }
    }
}

async Task BytesToWav()
{
    var lastPercent = 0d;

    var packetsDirectoryPath = PreparePacketsDir();

    var packets = new List<(short Index, byte[] Data)>();
    short packetIndex = 0;
    short packetLength = 0;
    short packetCount = 0;

    //read input data and separate it into packets that will be encoded separately so that partial data can be decoded
    await using (var stream = new FileStream(sourceDataPath, FileMode.Open))
    {
        packetCount = (short)(stream.Length / packetSize);
        if (stream.Length % packetSize != 0)
            packetCount++;

        var sourceData = new byte[packetSize];
        while ((packetLength = (short)await stream.ReadAsync(sourceData)) > 0)
        {
            var packetData = new byte[packetLength];
            Array.Copy(sourceData, packetData, packetLength);
            packets.Add((packetIndex++, packetData));
        }
    }

    await using var wavSourceStream = new WaveFileWriter(sourceWavPath, format);
    
    var samples = new float[audioHeaderPreambleSize * samplesPerSignal];
    var samplesPosition = (time: 0, offset: 0);
    
    samplesPosition = WriteSignalToSamples(samples, samplesPosition, (audioHeaderPreambleSize, packetSignal));
    wavSourceStream.WriteSamples(samples, 0, samplesPosition.offset);

    var packetHeaderSamplesSize = packetHeaderSize * 8 * 2;
    var packetSamplesSize = packetSize * 8 * 2;
    samples = new float[(packetHeaderSamplesSize + packetSamplesSize + audioPacketPreambleSize) * samplesPerSignal];

    var encodedPackets = 0;
    for (var i = 0; i < playCount; i++)
    {
        if (randomizePackets)
            packets = packets.OrderBy(_ => Guid.NewGuid()).ToList();
        foreach (var packet in packets)
        {
            encodedPackets++;
            packetIndex = packet.Index;
            packetLength = (short)packet.Data.Length;
            if (!File.Exists(Path.Combine(packetsDirectoryPath, $"packet.{packetIndex}")))
            {
                var packetCrc = CalculateArrayCrc(packet.Data, 0, packetLength);
                //ensure all data within the packet is accounted for it's hash
                packetCrc ^= packetIndex;
                packetCrc ^= packetCount;
                packetCrc ^= packetLength;

                var headerInfo = (packetIndex, packetCount, packetLength, packetCrc);
                samplesPosition = (samplesPosition.time, offset: 0);
                samplesPosition = WriteDataHeaderToSamples(samples, samplesPosition, headerInfo);
                samplesPosition = WriteDataToSamples(samples, samplesPosition, packet.Data, packetLength);
                samplesPosition = WriteSignalToSamples(samples, samplesPosition, (audioPacketPreambleSize, packetSignal));

                wavSourceStream.WriteSamples(samples, 0, samplesPosition.offset);
            }
            ReportCreateProgress(encodedPackets, playCount * packetCount);
        }
    }

    samples = new float[audioFooterPreambleSize * samplesPerSignal];
    samplesPosition = (samplesPosition.time, offset: 0);
    samplesPosition = WriteSignalToSamples(samples, samplesPosition, (audioFooterPreambleSize, packetSignal));
    wavSourceStream.WriteSamples(samples, 0, samplesPosition.offset);

    (int time, int offset) WriteDataHeaderToSamples(float[] samplesData, (int time, int offset) position, (short packetIndex, short packetCount, short packetLength, short packetCrc) header)
    {
        position = WriteDataToSamples(samplesData, position, BitConverter.GetBytes(header.packetIndex), sizeof(short));
        position = WriteDataToSamples(samplesData, position, BitConverter.GetBytes(header.packetCount), sizeof(short));
        position = WriteDataToSamples(samplesData, position, BitConverter.GetBytes(header.packetLength), sizeof(short));
        position = WriteDataToSamples(samplesData, position, BitConverter.GetBytes(header.packetCrc), sizeof(short));
        return position;
    }

    (int time, int offset) WriteDataToSamples(float[] samplesData, (int time, int offset) position, IReadOnlyList<byte> data, int dataLength)
    {
        for (var i = 0; i < dataLength; i++)
        {
            var dataItem = data[i];
            for (var bitPosition = 0; bitPosition < 8; bitPosition++)
            {
                var sampleBit = (dataItem >> bitPosition) & positive;
                var sampleSignal = sampleBit == negative ? negativeSignal : positiveSignal;
                position = WriteSignalToSamples(samplesData, position, (1, sampleSignal));
                position = WriteSignalToSamples(samplesData, position, (1, bitSignal));
            }
        }
        return position;
    }

    (int time, int offset) WriteSignalToSamples(float[] samplesData, (int time, int offset) position, (int count, int frequency) shape)
    {
        var i = position.offset;
        foreach(var sample in CreateWave(shape, position.time))
            samplesData[i++] = sample;
        var waveSamples = i - position.offset;
        return (position.time + waveSamples, i);
    }

    IEnumerable<float> CreateWave((int count, int frequency) shape, int time)
    {
        for (var i = 0; i < shape.count * samplesPerSignal; i++)
        {
            var t = i + time;
            var sample = (float)(amplitude * Math.Sin(2 * Math.PI * t * shape.frequency / samplesRate));
            yield return sample;
        }
    }

    void ReportCreateProgress(double progress, double total)
    {
        var percent = 1.0 * progress / total;
        if (percent - lastPercent > 0.1)
        {
            lastPercent = percent;
            Console.WriteLine("Created {0:P}", lastPercent);
        }
    }
}

async Task<int> WavToBytes()
{
    var lastPercent = 0d;

    var packetsDirectoryPath = PreparePacketsDir();    

    var decodedDataBuffer = new byte[packetHeaderSize + packetSize];
    var decodedDataBufferIndex = 0;

    byte decodedByte = 0;
    var packetsCount = 0;
    var bitPosition = 0;
    var signalRepeat = 0;
    var lastSignal = bitSeparator;

    await using var waveStream = new WaveFileReader(outputWavPath);
    if (segmentLength > waveStream.SampleCount)
        throw new IndexOutOfRangeException("Samples per signal should be less than length of an audio");
    var segment = new float[segmentLength];
    for (var i = segmentLength - segmentOverlap; i < segmentLength; i++)
        segment[i] = waveStream.ReadNextSampleFrame().Single();
    for (var i = segmentOverlap; i < waveStream.SampleCount - (segmentLength - segmentOverlap); i += segmentLength - segmentOverlap)
    {
        //shift segment as defined by overlap
        Array.Copy(segment, segmentLength - segmentOverlap, segment, 0, segmentOverlap);
        for (var j = segmentOverlap; j < segmentLength; j++)
            segment[j] = waveStream.ReadNextSampleFrame().Single();

        //parse signal
        var frequency = GetDominantFrequency(segment, 0, segment.Length);
        var signal = DecodeSignal(frequency);
        
        //skip if signal is unrecognized
        if (signal == -1)
            continue;            
        
        //count number of packet signals received and trigger packet complete logic only when it's reaching threshold        
        if (signal != packetSeparator)
            signalRepeat = 0;        
        if (signal == packetSeparator && signalRepeat++ < audioPacketPreambleDetection)        
            continue;

        //skip if next decoded signal is same as previously handled one
        if (signal == lastSignal)
            continue;

        //handle new signal
        if (signal == packetSeparator)
        {
            if (decodedDataBufferIndex > packetHeaderSize)
            {
                var packetsCountReceived = await TryWritePacket(decodedDataBuffer, decodedDataBufferIndex);
                if (packetsCountReceived >= 0)
                    packetsCount = packetsCountReceived;
            }
            //reset so we can start decoding next packet
            decodedDataBufferIndex = 0;
            decodedByte = 0;
            bitPosition = 0;
        }
        else
        {
            if (signal is negative or positive && lastSignal is bitSeparator or packetSeparator)
            {
                decodedByte |= (byte)(signal << bitPosition);
                bitPosition++;
            }
            if (bitPosition == 8)
            {
                if (decodedDataBufferIndex < decodedDataBuffer.Length)
                    decodedDataBuffer[decodedDataBufferIndex] = decodedByte;

                decodedDataBufferIndex++;
                decodedByte = 0;
                bitPosition = 0;
            }
        }
        lastSignal = signal;

        ReportDecodeProgress(i, waveStream.SampleCount);
    }
    return packetsCount;

    async Task<int> TryWritePacket(byte[] dataDecoded, int dataLength)
    {
        if (dataLength <= dataDecoded.Length)
        {
            short packetIndex = BitConverter.ToInt16(dataDecoded, sizeof(short) * 0);
            short packetCount = BitConverter.ToInt16(dataDecoded, sizeof(short) * 1);
            short packetLength = BitConverter.ToInt16(dataDecoded, sizeof(short) * 2);
            short packetCrc = BitConverter.ToInt16(dataDecoded, sizeof(short) * 3);

            var packetCrcReceived = CalculateArrayCrc(dataDecoded, packetHeaderSize, packetLength);
            packetCrcReceived ^= packetIndex;
            packetCrcReceived ^= packetCount;
            packetCrcReceived ^= packetLength;

            if (packetIndex >= 0 && packetCrcReceived == packetCrc && dataDecoded.Length - packetHeaderSize >= packetLength)
            {
                var packetId = $"packet.{packetIndex}";
                var packetOutPath = Path.Combine(packetsDirectoryPath, packetId);
                if (!File.Exists(packetOutPath))
                {
                    Console.WriteLine("Decoded packet: {0}", packetId);
                    await using var outputStream = new FileStream(packetOutPath, FileMode.Create);
                    await outputStream.WriteAsync(dataDecoded, packetHeaderSize, packetLength);
                }
                return packetCount;
            }

        }
        return -1;
    }

    int DecodeSignal(double frequency)
    {
        var signal = -1;
        if (InRange(frequency, 3000, 5000))
            signal = negative;
        else if (InRange(frequency, 11000, 13000))
            signal = positive;
        else if (InRange(frequency, 7000, 9000))
            signal = bitSeparator;
        else if (InRange(frequency, 15000, 19000))
            signal = packetSeparator;
        return signal;
    }

    bool InRange(double a, double start, double end)
        => a >= start && a <= end;

    double GetDominantFrequency(float[] waveSample, int offset, int count)
    {
        var data = new Complex[count];
        for (var i = offset; i < waveSample.Length && offset - i < count; i++)
            data[i - offset] = new Complex(waveSample[i], 0);

        Fourier.Forward(data);
        var maxIndex = -1;
        var maxMagnitude = double.MinValue;
        //start from 1 to skip 0hz bucket
        for (var i = 1; i < data.Length / 2 + 1; i++)
        {
            var magnitude = data[i].Magnitude;
            if (magnitude > maxMagnitude)
            {
                maxMagnitude = magnitude;
                maxIndex = i;
            }
        }
        return maxIndex * samplesRate / (double)count;
    }

    void ReportDecodeProgress(double progress, double total)
    {
        var percent = 1.0 * progress / total;
        if (percent - lastPercent > 0.1)
        {
            lastPercent = percent;
            Console.WriteLine("Decoded {0:P}", lastPercent);
        }
    }
}

async Task RecordBytes(IWaveIn waveIn, Func<IWaveIn, Task> playAction)
{
    await Task.Delay(TimeSpan.FromSeconds(1));
    var recordingTask = Task.Run(async () => {
        Console.WriteLine("Start recording");
        await RecordWav(waveIn);
        Console.WriteLine("Stop recording");
    });

    await Task.Delay(TimeSpan.FromSeconds(1));
    var playbackTask = Task.Run(async () => {
        Console.WriteLine("Start playing");
        await playAction.Invoke(waveIn);
        Console.WriteLine("Stop playing");
    });
    await Task.WhenAll(recordingTask, playbackTask);
}

short CalculateArrayCrc(byte[] array, int offset, int count)
{
    const short prime = 31;

    short hash = 0;
    for (var i = offset; i - offset < count && i < array.Length; i++)
        hash ^= (short)(array[i].GetHashCode() * prime);
    return hash;
}

string PreparePacketsDir()
{
    var baseDirName = Path.GetDirectoryName(outputDataPath);
    var baseFileName = Path.GetFileName(outputDataPath);
    var packetsDirectoryPath = Path.Combine(baseDirName!, baseFileName) + ".tmp";
    if (!Directory.Exists(packetsDirectoryPath))
        Directory.CreateDirectory(packetsDirectoryPath);
    return packetsDirectoryPath;
}

internal enum Mode
{
    Dual,
    Playing,
    Recording,    
    Decoding
}