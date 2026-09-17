using System;
using System.Collections.Generic;

namespace BeaverBuddies
{
    internal static class ReplayExecution
    {
        internal static void Run<T>(IEnumerable<T> events, Func<T, bool> replay,
            Action<T, Exception> failed, Action<bool> setActive, bool previousActive)
        {
            setActive(true);
            try
            {
                foreach (T item in events)
                {
                    try { if (!replay(item)) break; }
                    catch (Exception error) { failed(item, error); break; }
                }
            }
            finally { setActive(previousActive); }
        }
    }
}
