using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;

var Mask = 0x0;
var maskString = Console.ReadLine();
if (maskString != null)
{
    var hexString = maskString.StartsWith("0x", StringComparison.InvariantCultureIgnoreCase) 
        ? maskString[2..] 
        : maskString;
    Mask = Convert.ToByte(hexString, 16);
}

var sourceImage = "";
var decodedImage = "";
var dataIn = "";
var dataOut = "";

var dataLength = await EmbedData(sourceImage, decodedImage, dataIn);
await DecodeData(decodedImage, dataOut, dataLength);

Console.WriteLine("data length: {0}", dataLength);
Console.WriteLine("data mask: {0}", Mask.ToString("X4"));

async Task<long> EmbedData(string sourceImage, string decodedImage, string dataPath)
{
    Debug.Assert(OperatingSystem.IsWindows());
    await using var dataStream = new FileStream(dataPath, FileMode.Open);
    var image = (Bitmap)Image.FromFile(sourceImage);

    var data = new byte[dataStream.Length];
    await dataStream.ReadExactlyAsync(data, 0, data.Length);

    var totalPixels = image.Width * image.Height;
    var interval = totalPixels / (data.Length * 8 / 3);
    if (data.Length * 8 < totalPixels * 3)
        throw new InvalidOperationException("Image is too small to embed the data");
    
    var dataIndex = 0;
    var bitIndex = 0;
    var pixelCount = 0;
    for (var i = 0; i < data.Length; i++)
    {
        data[i] = (byte)(data[i] ^ Mask);
    }
    for (var y = 0; y < image.Height; y++)
    {
        for (var x = 0; x < image.Width; x++)
        {
            if (pixelCount % interval == 0 && dataIndex < data.Length)
            {
                var pixel = image.GetPixel(x, y);
                byte[] channels = { pixel.R, pixel.G, pixel.B };
                for (var channel = 0; channel < channels.Length && dataIndex < data.Length; channel++)
                {
                    channels[channel] = (byte)((channels[channel] & 0xFE) | ((data[dataIndex] >> (7 - bitIndex)) & 0x01));
                    bitIndex++;
                    if (bitIndex == 8)
                    {
                        bitIndex = 0;
                        dataIndex++;
                    }
                }
                var newPixel = Color.FromArgb(channels[0], channels[1], channels[2]);
                image.SetPixel(x, y, newPixel);
            }
            pixelCount++;
        }
    }
    if (File.Exists(decodedImage))
        File.Delete(decodedImage);
    image.Save(decodedImage!, ImageFormat.Png);
    return data.LongLength;
}

async Task DecodeData(string sourceImage, string dataOutPath, long dataSize)
{
    Debug.Assert(OperatingSystem.IsWindows());
    if (File.Exists(dataOutPath))
        File.Delete(dataOutPath);
    await using var dataOut = new FileStream(dataOutPath!, FileMode.CreateNew);
    using var image = (Bitmap)Image.FromFile(sourceImage);
    
    var data = new byte[dataSize];
    var totalPixels = image.Width * image.Height;
    var interval = totalPixels / (dataSize * 8 / 3);
    
    var dataIndex = 0;
    var bitIndex = 0;
    var pixelCount = 0;
    for (var y = 0; y < image.Height; y++)
    {
        for (var x = 0; x < image.Width; x++)
        {
            if (pixelCount % interval == 0 && dataIndex < dataSize)
            {
                var pixel = image.GetPixel(x, y);
                byte[] channels = { pixel.R, pixel.G, pixel.B };
                for (var channel = 0; channel <  channels.Length && dataIndex < dataSize; channel++)
                {
                    var extractedBit = (byte)(channels[channel] & 0x01);
                    data[dataIndex] = (byte)((data[dataIndex] & ~(0x01 << (7 - bitIndex))) | (extractedBit << (7 - bitIndex)));
                    bitIndex++;
                    if (bitIndex == 8)
                    {
                        bitIndex = 0;
                        dataIndex++;
                    }
                }
            }
            pixelCount++;
        }
    }   
    for (var i = 0; i < data.Length; i++)
    {
        data[i] = (byte)(data[i] ^ Mask);
    }
    await dataOut.WriteAsync(data);
}