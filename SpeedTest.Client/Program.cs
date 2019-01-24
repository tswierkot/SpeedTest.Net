using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Reflection.Metadata;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SpeedTest.Models;

namespace SpeedTest.Client
{
    class Program
    {
        private const string kLogFileName = "logs.csv";
        private const string kPingFileName = "pings.csv";
        private const string kTimeFormat = "dd.MM.yyyy hh:mm:ss";
        private const int kMaxFileSize = 1024 * 1024 * 100;
        private static SpeedTestClient client;
        private static Settings settings;
        private static readonly object lockObject = new object();
        private static readonly string[] pingHosts = new[] {"1.1.1.1", "8.8.8.8", "1.0.0.1", "8.8.4.4"};

        static void Main()
        {
            var t1 = Task.Factory.StartNew(SpeedTestLoop, TaskCreationOptions.LongRunning);
            var t2 = Task.Factory.StartNew(PingLoop, TaskCreationOptions.LongRunning);
            Task.WaitAny(t1, t2);
        }

        private static void PingLoop()
        {
            var pingIndex = 3;
            var hostCount = pingHosts.Length;
            var resultList = new List<long>(3);
            var pingSender = new Ping();
            var options = new PingOptions();
            options.DontFragment = true;
            byte[] sendData = Encoding.ASCII.GetBytes(new string('a', 32));
            var timeout = 2048;
            while (true)
            {
                var host = pingHosts[++pingIndex % hostCount];
                lock (lockObject)
                {
                    Console.WriteLine();
                    Console.WriteLine($"Ping test, host: {host}");
                    var time = DateTime.Now;
                    resultList.Clear();
                    for (int i = 0; i < 5; ++i)
                    {
                        PingReply reply = pingSender.Send(host, timeout, sendData, options);
                        switch (reply.Status)
                        {
                            case IPStatus.Success:
                                resultList.Add(reply.RoundtripTime);
                                break;
                            case IPStatus.TimedOut:
                            case IPStatus.TimeExceeded:
                            case IPStatus.TtlExpired:
                            case IPStatus.DestinationUnreachable:
                            case IPStatus.DestinationHostUnreachable:
                            case IPStatus.DestinationPortUnreachable:
                            case IPStatus.DestinationNetworkUnreachable:
                            case IPStatus.DestinationProtocolUnreachable:
                                resultList.Add(timeout);
                                break;
                        }
                    }

                    if (resultList.Count > 0)
                    {
                        var average = resultList.Sum() / resultList.Count;
                        LogResult(average, host, time);
                        Console.WriteLine($"Ping test, result: {average}");
                    }
                    else
                        Console.WriteLine($"Ping test, result: inconclusive");

                    Thread.Sleep(3000);
                }
            }
        }

        private static void SpeedTestLoop()
        {
            while (true)
            {
                lock (lockObject)
                {
                    double downloadSpeed = 0, uploadSpeed = 0;
                    Server server = null;
                    var time = DateTime.Now;
                    try
                    {
                        PerformTest(out downloadSpeed, out uploadSpeed, out server);
                    }
                    catch
                    {
                    }

                    try
                    {
                        LogResult(downloadSpeed, uploadSpeed, server, time);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Exception while trying to log result: {ex.Message}");
                        break;
                    }
                }

                Thread.Sleep(10000);
            }
        }

        private static void LogResult(long pingResult, string pingHost, DateTime time)
        {
            var curDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            var filePath = Path.Combine(curDir, kPingFileName);
            var fi = new FileInfo(filePath);
            if (fi.Exists && fi.Length > kMaxFileSize)
                throw new IOException($"File {filePath} is too damn big!");
            var retryCount = 5;
            var result = $"{time.ToString(kTimeFormat)};{pingResult};{pingHost}";
            while (retryCount-- > 0)
            {
                try
                {
                    using (var writer = File.AppendText(filePath))
                    {
                        writer.WriteLine(result);
                        break;
                    }
                }
                catch (Exception)
                {
                    if (retryCount > 0)
                        Thread.Sleep(1000);
                    else throw;
                }
            }
        }

        private static void LogResult(double downloadSpeed, double uploadSpeed, Server server, DateTime time)
        {
            var curDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            var filePath = Path.Combine(curDir, kLogFileName);
            var fi = new FileInfo(filePath);
            if (fi.Exists && fi.Length > kMaxFileSize)
                throw new IOException($"File {filePath} is too damn big!");
            var retryCount = 5;
            var result =
                $"{time.ToString(kTimeFormat)};{Math.Round(downloadSpeed / 1024, 2)};{Math.Round(uploadSpeed / 1024, 2)};{(server == null ? ";" : $"{server.Sponsor} {server.Name};{server.Latency}")}";
            while (retryCount-- > 0)
            {
                try
                {
                    using (var writer = File.AppendText(filePath))
                    {
                        writer.WriteLine(result);
                        break;
                    }
                }
                catch (Exception)
                {
                    if (retryCount > 0)
                        Thread.Sleep(1000);
                    else throw;
                }
            }
        }

        static void PerformTest(out double downloadSpeed, out double uploadSpeed, out Server server)
        {
            Console.WriteLine("Getting speedtest.net settings and server list...");
            client = new SpeedTestClient();
            settings = client.GetSettings();

            var servers = SelectServers();
            var bestServer = SelectBestServer(servers);

            Console.WriteLine("Testing speed...");
            var ds = client.TestDownloadSpeed(bestServer, settings.Download.ThreadsPerUrl);
            PrintSpeed("Download", ds);
            var us = client.TestUploadSpeed(bestServer, settings.Upload.ThreadsPerUrl);
            PrintSpeed("Upload", us);

            downloadSpeed = ds;
            uploadSpeed = us;
            server = bestServer;
        }

        private static Server SelectBestServer(IEnumerable<Server> servers)
        {
            Console.WriteLine();
            Console.WriteLine("Best server by latency:");
            var bestServer = servers.OrderBy(x => x.Latency).First();
            PrintServerDetails(bestServer);
            Console.WriteLine();
            return bestServer;
        }

        private static IEnumerable<Server> SelectServers()
        {
            Console.WriteLine();
            Console.WriteLine("Selecting best server by distance...");
            var servers = settings.Servers.Take(10).ToList();

            foreach (var server in servers)
            {
                server.Latency = client.TestServerLatency(server);
                PrintServerDetails(server);
            }

            return servers;
        }

        private static void PrintServerDetails(Server server)
        {
            Console.WriteLine("Hosted by {0} ({1}/{2}), distance: {3}km, latency: {4}ms", server.Sponsor, server.Name,
                server.Country, (int) server.Distance / 1000, server.Latency);
        }

        private static void PrintSpeed(string type, double speed)
        {
            if (speed > 1024)
            {
                Console.WriteLine("{0} speed: {1} Mbps", type, Math.Round(speed / 1024, 2));
            }
            else
            {
                Console.WriteLine("{0} speed: {1} Kbps", type, Math.Round(speed, 2));
            }
        }
    }
}