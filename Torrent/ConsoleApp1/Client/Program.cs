using System;
using System.IO;
using BitTorent;
using Mono.Unix;
using Mono.Unix.Native;

namespace ConsoleApp1
{
    public static class Program
    {
        private static Client _client;

        public static void Main(string[] args)
        {
            if (args.Length != 3 || !Int32.TryParse(args[0], out var port) || !File.Exists(args[1]))
            {
                Console.WriteLine("Error: requires port, torrent file and download directory as first, second and third arguments");
                return;
            }

            _client = new Client(port, args[1], args[2]);
            _client.Start();

            new UnixSignal(Signum.SIGINT).WaitOne();
            _client.Stop();
        }
    }
}
