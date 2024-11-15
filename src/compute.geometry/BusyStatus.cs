using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace compute.geometry
{
    public static class BusyStatus
    {
        private static int _activeRequests = 0;

        public static void Increment()
        {
            Interlocked.Increment(ref _activeRequests);
        }

        public static void Decrement()
        {
            Interlocked.Decrement(ref _activeRequests);
        }

        public static int GetActiveRequestCount()
        {
            return _activeRequests;
        }
    }
}
