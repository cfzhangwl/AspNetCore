using FASTER.core;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace WebCore.Cache
{
    /// <summary>
    /// Faster Key/Value is a fast concurrent persistent log and key-value store with cache for larger-than-memory data.
    /// https://github.com/microsoft/FASTER
    /// </summary>
    /// <typeparam name="TKey">Recommend string</typeparam>
    /// <typeparam name="TValue">Custom class</typeparam>
    public class KV<TKey, TValue> where TValue : new()
    {
        private readonly long size;
        private readonly string _path;
        private readonly IDevice _log;
        private readonly IDevice _obj;
        private readonly FasterKV<TKey, TValue> _fht;
        private readonly ClientSession<TKey, TValue, TValue, TValue, Empty, IFunctions<TKey, TValue, TValue, TValue, Empty>> _session;
        public SimpleFunctions<TKey, TValue> fn = new SimpleFunctions<TKey, TValue>();

        /// <summary>
        /// Faster Key/Value in-memory cache
        /// </summary>
        /// <param name="size">size of hash table in #cache lines; 64 bytes per cache line</param>
        /// <param name="serializerSettings">Sets a new SerializerSettings { keySerializer = () => new KeySerializer(), valueSerializer = () => new ValueSerializer() }</param>
        public KV(long size = 1L << 20, SerializerSettings<TKey, TValue> serializerSettings = null)
        {
            this.size = size;
            _log = new NullDevice();
            _obj = new NullDevice();
            var logSettings = new LogSettings { LogDevice = _log, ObjectLogDevice = _obj };
            _fht = new FasterKV<TKey, TValue>(size, logSettings, null, serializerSettings);
            _session = NewSession();
        }

        /// <summary>
        /// Faster Key/Value in-memory and disk cache store
        /// </summary>
        /// <param name="path">the directory path of cache</param>
        /// <param name="size">hash table size (number of 64-byte buckets, each bucket is 64 bytes, 1 << 20 = 340M snapshot file)</param>
        /// <param name="pageSizeBits">Size of a segment (group of pages), in bits: 9, 15, 22</param>
        /// <param name="memorySizeBits">Total size of in-memory part of log, in bits: 14, 20, 30</param>
        /// <param name="mutableFraction">Fraction of log marked as mutable (in-place updates): 0.1, 0.2, 0.3</param>
        /// <param name="serializerSettings">Sets a new SerializerSettings { keySerializer = () => new KeySerializer(), valueSerializer = () => new ValueSerializer() }</param>
        /// <param name="seconds">Flush current log, Interval more than 2 seconds to issue periodic checkpoints</param>
        public KV(string path = null, long size = 1L << 20, int pageSizeBits = 22, int memorySizeBits = 30, double mutableFraction = 0.3, SerializerSettings<TKey, TValue> serializerSettings = null, int seconds = 0)
        {
            if (string.IsNullOrEmpty(path)) path = Path.GetTempPath();
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);

            var dirname = typeof(TValue).Name;
            if (!Path.GetDirectoryName(path).EndsWith(dirname, StringComparison.OrdinalIgnoreCase)) path = Path.Combine(path, dirname);
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);

            _path = path;
            _log = Devices.CreateLogDevice(Path.Combine(path, "cache.log"));
            _obj = Devices.CreateLogDevice(Path.Combine(path, "cache.obj.log"));

            this.size = size;

            var checkpointDir = new DirectoryInfo(Path.Combine(path, "checkpoints"));
            var checkpointSettings = new CheckpointSettings
            {
                CheckpointDir = checkpointDir.FullName,
                CheckPointType = CheckpointType.Snapshot
            };
            var logSettings = new LogSettings
            {
                LogDevice = _log,
                ObjectLogDevice = _obj,
                PageSizeBits = pageSizeBits,
                MemorySizeBits = memorySizeBits,
                MutableFraction = mutableFraction
            };

            _fht = new FasterKV<TKey, TValue>(size, logSettings, checkpointSettings, serializerSettings);
            KVCache.Recover(_fht, checkpointDir);
            _session = NewSession();
            if (seconds > 2) IssuePeriodicCheckpoints(seconds * 1000);
        }

        private void IssuePeriodicCheckpoints(int milliseconds)
        {
            var t = new Thread(() =>
            {
                while (true)
                {
                    Thread.Sleep(milliseconds);
                    (_, _) = _fht.TakeHybridLogCheckpointAsync(CheckpointType.FoldOver).GetAwaiter().GetResult();
                }
            })
            { IsBackground = true };
            t.Start();
        }

        public FasterKV<TKey, TValue> GetCache()
        {
            return _fht;
        }

        public ClientSession<TKey, TValue, TValue, TValue, Empty, IFunctions<TKey, TValue, TValue, TValue, Empty>> GetSession()
        {
            return _session;
        }

        public string GetDirectory()
        {
            return _path;
        }

        public ClientSession<TKey, TValue, TValue, TValue, Empty, IFunctions<TKey, TValue, TValue, TValue, Empty>> NewSession(string sessionId = null, bool threadAffinitized = false, SimpleFunctions<TKey, TValue> fn = null)
        {
            return _fht.NewSession(fn ?? this.fn, sessionId, threadAffinitized);
        }

        public ClientSession<TKey, TValue, TValue, TValue, Empty, IFunctions<TKey, TValue, TValue, TValue, Empty>> ResumeSession(string sessionId, out CommitPoint commitPoint, bool threadAffinitized = false, SimpleFunctions<TKey, TValue> fn = null)
        {
            return _fht.ResumeSession(fn ?? this.fn, sessionId, out commitPoint, threadAffinitized);
        }

        public TValue Get(TKey key, bool wait = false)
        {
            return Get(key, wait, _session);
        }

        public TValue Get(TKey key, bool wait, ClientSession<TKey, TValue, TValue, TValue, Empty, IFunctions<TKey, TValue, TValue, TValue, Empty>> session)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));

            var (status, output) = session.Read(key);

            return status == Status.OK ? output : status == Status.PENDING && wait == true && session.CompletePending(true) ? Get(key, wait, session) : default;
        }

        public async ValueTask<TValue> GetAsync(TKey key, bool wait = false)
        {
            return await GetAsync(key, wait, _session);
        }

        public async ValueTask<TValue> GetAsync(TKey key, bool wait, ClientSession<TKey, TValue, TValue, TValue, Empty, IFunctions<TKey, TValue, TValue, TValue, Empty>> session)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));

            var result = await session.ReadAsync(key);

            var (status, output) = result.Complete();

            return status == Status.OK ? output : status == Status.PENDING && wait == true && session.CompletePending(true) ? await GetAsync(key, wait, session) : default;
        }

        public bool Set(TKey key, TValue value, bool wait = false)
        {
            return Set(key, value, wait, _session);
        }

        public bool Set(TKey key, TValue value, bool wait, ClientSession<TKey, TValue, TValue, TValue, Empty, IFunctions<TKey, TValue, TValue, TValue, Empty>> session)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));

            var status = session.Upsert(key, value);

            return status == Status.OK || (status == Status.PENDING && wait == true && session.CompletePending(true));
        }

        public async Task<bool> SetAsync(TKey key, TValue value, bool wait = false)
        {
            return await SetAsync(key, value, wait, _session);
        }

        public async Task<bool> SetAsync(TKey key, TValue value, bool wait, ClientSession<TKey, TValue, TValue, TValue, Empty, IFunctions<TKey, TValue, TValue, TValue, Empty>> session)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));

            var result = await session.RMWAsync(key, value);

            (Status status, _) = result.Complete();

            return status == Status.OK || (status == Status.PENDING && wait == true && session.CompletePending(true));
        }

        public bool Delete(TKey key, bool wait = false)
        {
            return Delete(key, wait, _session);
        }

        public bool Delete(TKey key, bool wait, ClientSession<TKey, TValue, TValue, TValue, Empty, IFunctions<TKey, TValue, TValue, TValue, Empty>> session)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));

            var status = session.Delete(key);

            return status == Status.OK || (status == Status.PENDING && wait == true && session.CompletePending(true));
        }

        public async Task<bool> DeleteAsync(TKey key, bool wait = false)
        {
            return await DeleteAsync(key, wait, _session);
        }

        public async Task<bool> DeleteAsync(TKey key, bool wait, ClientSession<TKey, TValue, TValue, TValue, Empty, IFunctions<TKey, TValue, TValue, TValue, Empty>> session)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));

            var result = await session.DeleteAsync(key);

            var status = result.Complete();

            return status == Status.OK || (status == Status.PENDING && wait == true && session.CompletePending(true));
        }


        /// <summary>
        /// Set value
        /// </summary>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <param name="wait">Wait for all pending operations on session to complete</param>
        /// <param name="spinWaitForCommit">Spin-wait until ongoing commit/checkpoint, if any, completes</param>
        /// <returns>True if update and pending operation have completed, false otherwise</returns>
        public bool SetFor(TKey key, TValue value, bool wait = false, bool spinWaitForCommit = false)
        {
            using (var s = _fht.For(fn).NewSession<SimpleFunctions<TKey, TValue>>())
            {
                var status = s.Upsert(ref key, ref value);
                return status == Status.OK && s.CompletePending(wait, spinWaitForCommit);
            }
        }
        /// <summary>
        /// Set value
        /// </summary>
        public bool SetFor(TKey key, TValue value, string sessionId, bool resume = false, bool wait = false, bool spinWaitForCommit = false)
        {
            using (var s = resume == true
                ? _fht.For(fn).ResumeSession<SimpleFunctions<TKey, TValue>>(sessionId, out _)
                : _fht.For(fn).NewSession<SimpleFunctions<TKey, TValue>>(sessionId))
            {
                var status = s.Upsert(ref key, ref value);
                return status == Status.OK && s.CompletePending(wait, spinWaitForCommit);
            }
        }

        /// <summary>
        /// Get value
        /// </summary>
        /// <param name="key"></param>
        /// <returns></returns>
        public TValue GetFor(TKey key)
        {
            using (var s = _fht.For(fn).NewSession<SimpleFunctions<TKey, TValue>>())
            {
                var valueOut = new TValue();
                var status = s.Read(ref key, ref valueOut);
                return status == Status.OK ? valueOut : default;
            }
        }
        /// <summary>
        /// Get value
        /// </summary>
        public TValue GetFor(TKey key, string sessionId, bool resume = false)
        {
            using (var s = resume == true
                ? _fht.For(fn).ResumeSession<SimpleFunctions<TKey, TValue>>(sessionId, out _)
                : _fht.For(fn).NewSession<SimpleFunctions<TKey, TValue>>(sessionId))
            {
                var valueOut = new TValue();
                var status = s.Read(ref key, ref valueOut);
                return status == Status.OK ? valueOut : default;
            }
        }

        /// <summary>
        /// Delete value
        /// </summary>
        /// <param name="key"></param>
        /// <returns>OK = 0, NOTFOUND = 1, PENDING = 2, ERROR = 3</returns>
        public int DeleteFor(TKey key)
        {
            using (var s = _fht.For(fn).NewSession<SimpleFunctions<TKey, TValue>>())
            {
                return (int)s.Delete(ref key);
            }
        }
        /// <summary>
        /// Delete value
        /// </summary>
        public int DeleteFor(TKey key, string sessionId, bool resume = false)
        {
            using (var s = resume == true
                ? _fht.For(fn).ResumeSession<SimpleFunctions<TKey, TValue>>(sessionId, out _)
                : _fht.For(fn).NewSession<SimpleFunctions<TKey, TValue>>(sessionId))
            {
                return (int)s.Delete(ref key);
            }
        }

        /// <summary>
        /// Complete outstanding pending operations
        /// </summary>
        /// <param name="wait">Wait for all pending operations on session to complete</param>
        /// <param name="spinWaitForCommit">Spin-wait until ongoing commit/checkpoint, if any, completes</param>
        /// <returns>True if all pending operations have completed, false otherwise</returns>
        public bool CompletePending(bool wait = false, bool spinWaitForCommit = false)
        {
            using (var s = _fht.For(fn).NewSession<SimpleFunctions<TKey, TValue>>())
            {
                return s.CompletePending(wait, spinWaitForCommit);
            }
        }

        /// <summary>
        /// Save snapshot and wait for ongoing full checkpoint to complete
        /// </summary>
        /// <param name="dispose"></param>
        /// <returns>Checkpoint token</returns>
        public async Task<Guid> SaveSnapshot(bool dispose = true)
        {
            Guid token = Guid.Empty;
            if (string.IsNullOrEmpty(_path)) return token;
            while (!_fht.TakeFullCheckpoint(out token)) CompletePending(true, true);
            await _fht.CompleteCheckpointAsync();
            var filename = Path.Combine(_path, $"{size}.checkpoint");
            File.WriteAllText(filename, token.ToString(), System.Text.Encoding.UTF8);
            if (!dispose) return token;
            _fht.Dispose();
            _log.Dispose();
            _obj.Dispose();
            return token;
        }

        /// <summary>
        /// Hashtable dispose and wait for ongoing checkpoint to complete
        /// </summary>
        /// <returns>Checkpoint token</returns>
        public async Task<Guid> Dispose()
        {
            return await SaveSnapshot(true);
        }
    }
}
