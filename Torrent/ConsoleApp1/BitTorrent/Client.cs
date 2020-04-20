using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using BitTorrent;

namespace BitTorent
{
    public class Client
    {
        #region Parameters

        private int Port { get; set; }
        private Torrent Torrent { get; set; }

        private static readonly int MaxLeechers = 5;
        private const int MaxSeeders = 5;

        private static int maxUploadBytesPerSecond = 16384;
        private static int maxDownloadBytesPerSecond = 16384;

        private static readonly TimeSpan PeerTimeout = TimeSpan.FromSeconds(30);

        #endregion

        private string Id { get; set; }

        private TcpListener _listener;

        private ConcurrentDictionary<string,Peer> Peers { get; } = new ConcurrentDictionary<string,Peer>();
        private ConcurrentDictionary<string,Peer> Seeders { get; } = new ConcurrentDictionary<string,Peer>();
        private ConcurrentDictionary<string,Peer> Leechers { get; } = new ConcurrentDictionary<string,Peer>();

        private bool _isStopping;
        private int _isProcessPeers;
        private int _isProcessUploads;
        private int _isProcessDownloads;

        private readonly ConcurrentQueue<DataRequest> _outgoingBlocks = new ConcurrentQueue<DataRequest>();
        private readonly ConcurrentQueue<DataPackage> _incomingBlocks = new ConcurrentQueue<DataPackage>();

        private readonly Throttle _uploadThrottle = new Throttle(maxUploadBytesPerSecond, TimeSpan.FromSeconds(1));
        private readonly Throttle _downloadThrottle = new Throttle(maxDownloadBytesPerSecond, TimeSpan.FromSeconds(1));

        private readonly Random _random = new Random();

        public Client(int port, string torrentPath, string downloadPath)
        {
            // generate random numerical id
            Id = "";
            for (int i = 0; i < 20; i++)
                Id += (_random.Next(0, 10));

            Port = port;

            Torrent = Torrent.LoadFromFile(torrentPath, downloadPath);
            Torrent.PieceVerified += HandlePieceVerified;
            Torrent.PeerListUpdated += HandlePeerListUpdated;

            Log.WriteLine(Torrent);
        }

        public void Start()
        {
            Log.WriteLine("starting client");

            _isStopping = false;

            Torrent.ResetTrackersLastRequest();

            EnablePeerConnections();

            // tracker thread
            new Thread(() =>
            {
                while (!_isStopping)
                {
                    Torrent.UpdateTrackers(TrackerEvent.Started, Id, Port);
                    Thread.Sleep(10000);
                }
            }).Start();

            // peer thread
            new Thread(() =>
            { 
                while (!_isStopping)
                {
                    ProcessPeers();
                    Thread.Sleep(1000);
                }
            }).Start();

            // upload thread
            new Thread(() =>
            { 
                while (!_isStopping)
                {
                    ProcessUploads();
                    Thread.Sleep(1000);
                }
            }).Start();

            // download thread
            new Thread(() =>
            { 
                while (!_isStopping)
                {
                    ProcessDownloads();
                    Thread.Sleep(1000);
                }
            }).Start();
        }

        public void Stop()
        {
            Log.WriteLine("stopping client");

            _isStopping = true;
            DisablePeerConnections();
            Torrent.UpdateTrackers(TrackerEvent.Stopped, Id, Port);
        }

        

        private static IPAddress LocalIpAddress
        {
            get
            {
                var host = Dns.GetHostEntry(Dns.GetHostName());
                foreach (var ip in host.AddressList)
                {
                    if (ip.AddressFamily == AddressFamily.InterNetwork)
                        return ip;                
                }
                throw new Exception("Local IP Address Not Found!");
            }
        }

        private void HandlePeerListUpdated(object sender, List<IPEndPoint> endPoints)
        {
            IPAddress local = LocalIpAddress;

            foreach(var endPoint in endPoints)
            {
                if (endPoint.Address.Equals(local) && endPoint.Port == Port)
                    continue;
                
                AddPeer(new Peer(Torrent, Id, endPoint));
            }

            Log.WriteLine("received peer information from " + (Tracker)sender);
            Log.WriteLine("peer count: " + Peers.Count);
        }

        private void EnablePeerConnections()
        {
            _listener = new TcpListener(new IPEndPoint(IPAddress.Any, Port));
            _listener.Start();
            _listener.BeginAcceptTcpClient(HandleNewConnection, null);

            Log.WriteLine("started listening for incoming peer connections on port " + Port);
        }

        private void HandleNewConnection(IAsyncResult ar)
        {
            if (_listener == null)
                return;

            TcpClient client = _listener.EndAcceptTcpClient(ar);
            _listener.BeginAcceptTcpClient(HandleNewConnection, null);

            AddPeer(new Peer(Torrent, Id, client));
        }

        private void DisablePeerConnections()
        {
            _listener.Stop();
            _listener = null;

            foreach (var peer in Peers)
                peer.Value.Disconnect();

            Log.WriteLine("stopped listening for incoming peer connections on port " + Port);
        }

        private void AddPeer(Peer peer)
        {
            peer.BlockRequested += HandleBlockRequested;
            peer.BlockCancelled += HandleBlockCancelled;
            peer.BlockReceived += HandleBlockReceived;
            peer.Disconnected += HandlePeerDisconnected;
            peer.StateChanged += HandlePeerStateChanged;

            peer.Connect();

            if (!Peers.TryAdd(peer.Key, peer))
                peer.Disconnect();
        }

        private void HandlePeerDisconnected(object sender, EventArgs args)
        {
            if (sender is Peer peer)
            {
                peer.BlockRequested -= HandleBlockRequested;
                peer.BlockCancelled -= HandleBlockCancelled;
                peer.BlockReceived -= HandleBlockReceived;
                peer.Disconnected -= HandlePeerDisconnected;
                peer.StateChanged -= HandlePeerStateChanged;

                Peers.TryRemove(peer.Key, out _);
                Seeders.TryRemove(peer.Key, out _);
                Leechers.TryRemove(peer.Key, out _);
            }
        }

        private void HandlePeerStateChanged(object sender, EventArgs args)
        {
            ProcessPeers();
        }

        private void HandlePieceVerified (object sender, int index)
        {
            ProcessPeers();

            foreach (var peer in Peers)
            {
                if (!peer.Value.IsHandshakeReceived || !peer.Value.IsHandshakeSent)
                    continue;

                peer.Value.SendHave(index);
            }
        }

        private void ProcessPeers()
        {
            if (Interlocked.Exchange(ref _isProcessPeers, 1) == 1)
                return;

            foreach (var peer in Peers.OrderByDescending(x => x.Value.PiecesRequiredAvailable))
            {
                if (DateTime.UtcNow > peer.Value.LastActive.Add(PeerTimeout))
                {                    
                    peer.Value.Disconnect();
                    continue;
                }

                if (!peer.Value.IsHandshakeSent || !peer.Value.IsHandshakeReceived)
                    continue;

                if (Torrent.IsCompleted)
                    peer.Value.SendNotInterested();
                else
                    peer.Value.SendInterested();

                if (peer.Value.IsCompleted && Torrent.IsCompleted)
                {
                    peer.Value.Disconnect();
                    continue;
                }

                peer.Value.SendKeepAlive();

                // let them leech
                if (Torrent.IsStarted && Leechers.Count < MaxLeechers)
                {
                    if (peer.Value.IsInterestedReceived && peer.Value.IsChokeSent)
                        peer.Value.SendUnchoke();                
                }

                // ask to leech
                if (!Torrent.IsCompleted && Seeders.Count <= MaxSeeders)
                {
                    if(!peer.Value.IsChokeReceived )
                        Seeders.TryAdd(peer.Key, peer.Value);
                }
            }

            Interlocked.Exchange(ref _isProcessPeers, 0);
        }


        

        private void HandleBlockRequested(object sender, DataRequest block)
        {
            _outgoingBlocks.Enqueue(block);

            ProcessUploads();
        }

        private void HandleBlockCancelled(object sender, DataRequest block)
        {
            foreach (var item in _outgoingBlocks)
            {
                if (item.Peer != block.Peer || item.Piece != block.Piece || item.Begin != block.Begin || item.Length != block.Length)
                    continue;

                item.IsCancelled = true;
            }

            ProcessUploads();
        }

        private void ProcessUploads()
        {
            if (Interlocked.Exchange(ref _isProcessUploads, 1) == 1)
                return;

            while (!_uploadThrottle.IsThrottled && _outgoingBlocks.TryDequeue(out var block))
            {
                if (block.IsCancelled)
                    continue;

                if (!Torrent.IsPieceVerified[block.Piece])
                    continue;            

                byte[] data = Torrent.ReadBlock(block.Piece, block.Begin, block.Length);
                if (data == null)
                    continue;

                block.Peer.SendPiece(block.Piece, block.Begin, data);
                _uploadThrottle.Add(block.Length);
                Torrent.Uploaded += block.Length;
            }

            Interlocked.Exchange(ref _isProcessUploads, 0);
        }

    

        private void HandleBlockReceived(object sender, DataPackage args)
        {
            _incomingBlocks.Enqueue(args);

            args.Peer.IsBlockRequested[args.Piece][args.Block] = false;

            foreach(var peer in Peers)
            {
                if (!peer.Value.IsBlockRequested[args.Piece][args.Block])
                    continue;

                peer.Value.SendCancel(args.Piece, args.Block * Torrent.BlockSize, Torrent.BlockSize);
                peer.Value.IsBlockRequested[args.Piece][args.Block] = false;
            }

            ProcessDownloads();
        }

        private void ProcessDownloads()
        {
            if (Interlocked.Exchange(ref _isProcessDownloads, 1) == 1)
                return;

            while (_incomingBlocks.TryDequeue(out var incomingBlock))
                Torrent.WriteBlock(incomingBlock.Piece, incomingBlock.Block, incomingBlock.Data);

            if (Torrent.IsCompleted)
            {
                Interlocked.Exchange(ref _isProcessDownloads, 0);
                return;
            }

            int[] ranked = GetRankedPieces();

            foreach (var piece in ranked)
            {
                if (Torrent.IsPieceVerified[piece])
                    continue;

                foreach (var peer in GetRankedSeeders())
                {
                    if (!peer.IsPieceDownloaded[piece])
                        continue;

                    // just request blocks in order
                    for (int block = 0; block < Torrent.GetBlockCount(piece); block++)
                    {                        
                        if (_downloadThrottle.IsThrottled)
                            continue;

                        if(Torrent.IsBlockAcquired[piece][block])
                            continue;

                        // only request one block from each peer at a time
                        if (peer.BlocksRequested > 0)
                            continue;

                        // only request from 1 peer at a time
                        if (Peers.Count(x => x.Value.IsBlockRequested[piece][block]) > 0)
                            continue;

                        int size = Torrent.GetBlockSize(piece, block);
                        peer.SendRequest(piece, block * Torrent.BlockSize, size);
                        _downloadThrottle.Add(size);
                        peer.IsBlockRequested[piece][block] = true;
                    }
                }
            }

            Interlocked.Exchange(ref _isProcessDownloads, 0);
        }

        private int[] GetRankedPieces()
        {
            var indexes = Enumerable.Range(0, Torrent.PieceCount).ToArray();
            var scores = indexes.Select(GetPieceScore).ToArray();
                
            Array.Sort(scores, indexes);
            Array.Reverse(indexes);

            return indexes;
        }

        private double GetPieceScore(int piece)
        {
            double progress = GetPieceProgress(piece);
            double rarity = GetPieceRarity(piece);

            if( progress == 1.0 )
                progress = 0;

            double rand = _random.Next(0,100) / 1000.0;

            return progress + rarity + rand;
        }

        private double GetPieceProgress(int index)
        {
            return Torrent.IsBlockAcquired[index].Average(x => x ? 1.0 : 0.0);
        }

        private double GetPieceRarity(int index)
        {
            if(Peers.Count < 1 )
                return 0.0;

            return Peers.Average(x => x.Value.IsPieceDownloaded[index] ? 0.0 : 1.0);
        }

        private Peer[] GetRankedSeeders()
        {
            return Seeders.Values.OrderBy(x => _random.Next(0, 100)).ToArray();
        }

        
    }
}