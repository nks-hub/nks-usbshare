using NksUsbShare.Tests;

Console.WriteLine();
Console.WriteLine("NKS UsbShare test suite");
Console.WriteLine($"  runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
Console.WriteLine($"  os:      {System.Runtime.InteropServices.RuntimeInformation.OSDescription} " +
                  $"({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture})");

ProtocolTests.Register();
UsbTests.Register();
LiveHardwareTests.Register();

var filter = args.FirstOrDefault(a => !a.StartsWith('-'));
return await TestRunner.RunAsync(filter);
