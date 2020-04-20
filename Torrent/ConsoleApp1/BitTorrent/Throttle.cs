using System;
using System.Collections.Generic;
using System.Linq;

namespace BitTorent
{
    public class Throttle
    {
        private long MaximumSize { get; set; }
        private TimeSpan MaximumWindow { get; set; }

        private struct Item
        {
            public DateTime Time;
            public long Size;
        }

        private readonly object _itemLock = new object();
        private readonly List<Item> _items = new List<Item>();

        public Throttle(int maxSize, TimeSpan maxWindow)
        {
            MaximumSize = maxSize;
            MaximumWindow = maxWindow;
        }

        public void Add(long size)
        {
            lock (_itemLock)
            {
                _items.Add(new Item() { Time = DateTime.UtcNow, Size = size });
            }
        }

        public bool IsThrottled
        {
            get
            { 
                lock (_itemLock)
                {
                    DateTime cutoff = DateTime.UtcNow.Add(-this.MaximumWindow);
                    _items.RemoveAll(x => x.Time < cutoff);
                    return _items.Sum(x => x.Size) >= MaximumSize;
                }
            }
        }
    }
}