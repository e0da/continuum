using System;
using System.Collections.Generic;

namespace KspContinuum.Mission
{
    public sealed class MissionCleanup
    {
        readonly List<KeyValuePair<string, Action>> owned = new List<KeyValuePair<string, Action>>();

        public void Track(string name, Action release)
        {
            if (release == null) throw new ArgumentNullException("release");
            if (owned.Exists(item => item.Key == name)) return;
            owned.Add(new KeyValuePair<string, Action>(name, release));
        }

        public Exception Release(string name)
        {
            int index = owned.FindIndex(item => item.Key == name);
            if (index < 0) return null;
            Action release = owned[index].Value;
            owned.RemoveAt(index);
            try { release(); return null; }
            catch (Exception error) { return error; }
        }

        public IReadOnlyList<Exception> ReleaseAll()
        {
            var failures = new List<Exception>();
            while (owned.Count != 0)
            {
                Exception error = Release(owned[owned.Count - 1].Key);
                if (error != null) failures.Add(error);
            }
            return failures;
        }
    }
}
