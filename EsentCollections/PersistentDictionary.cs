// --------------------------------------------------------------------------------------------------------------------
// <copyright file="PersistentDictionary.cs" company="Microsoft Corporation">
//   Copyright (c) Microsoft Corporation.
// </copyright>
// <summary>
//   Implementation of the PersistentDictionary. The dictionary is a collection
//   of persistent keys and values.
// </summary>
// --------------------------------------------------------------------------------------------------------------------

namespace Microsoft.Isam.Esent.Collections.Generic
{
    using System;
    using System.Collections;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Diagnostics.CodeAnalysis;
    using System.Diagnostics.Contracts;
    using System.Globalization;
    using System.IO;
    using System.Text;
    using System.Threading;
    using Microsoft.Database.Isam;
    using Microsoft.Database.Isam.Config;
    using Microsoft.Isam.Esent.Interop;
    using Microsoft.Isam.Esent.Interop.Windows7;

    /// <summary>
    /// Represents a collection of persistent keys and values.
    /// </summary>
    /// <typeparam name="TKey">The type of the key.</typeparam>
    /// <typeparam name="TValue">The type of the value.</typeparam>
    [SuppressMessage("Exchange.Performance",
        "EX0023:DeadVariableDetector",
        Justification = "databasePath is useful for debugging.")]
    public sealed partial class PersistentDictionary<TKey, TValue> : IDictionary<TKey, TValue>, IDisposable
        where TKey : IComparable<TKey>
    {
        /// <summary>
        /// Number of lock objects. Keys are mapped to lock objects using their
        /// hash codes. Making this count a prime number reduces the chance of
        /// collisions.
        /// </summary>
        private const int NumUpdateLocks = 31;

        /// <summary>
        /// The ESENT instance this dictionary uses. An Instance object inherits
        /// from SafeHandle so this instance will be (eventually) terminated even
        /// if the dictionary isn't disposed. 
        /// </summary>
        private readonly Instance instance;

        /// <summary>
        /// An update lock should be taken when the Dictionary is being updated. 
        /// Read operations can proceed without any locks (the cursor cache has
        /// its own lock to control access to the cursors). There are multiple
        /// update locks, which allows multiple writers. When updating a key
        /// take the lock which maps to key.GetHashCode() % updateLocks.Length.
        /// </summary>
        private readonly object[] updateLocks;

        /// <summary>
        /// The disposeLock ensures safety when Disposing an object in a multi-threaded
        /// environment.
        /// All public access points of entry acquire a reader lock.
        /// Once this is acquired, it calls <see cref="CheckObjectDisposed"/>.
        /// <see cref="Dispose(bool)"/> enters as a writer, ensuring that
        /// any call in progress will complete. While it is disposing, new calls
        /// will be blocked.
        /// </summary>
        /// <remarks>
        /// Consider moving to NoRecursion.
        /// </remarks>
        private readonly ReaderWriterLockSlim disposeLock =
            new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);

        /// <summary>
        /// Methods to set and retrieve data in ESE.
        /// </summary>
        private readonly PersistentDictionaryConverters<TKey, TValue> converters;

        /// <summary>
        /// Meta-data information for the dictionary database.
        /// </summary>
        private readonly IPersistentDictionaryConfig schema;

        /// <summary>
        /// Cache of cursors used to access the dictionary.
        /// </summary>
        private readonly PersistentDictionaryCursorCache<TKey, TValue> cursors;

        /// <summary>
        /// Path to the database.
        /// </summary>
        private readonly string databaseDirectory;

        /// <summary>
        /// Path to the database.
        /// </summary>
        private readonly string databasePath;

        // !EFW - Added support for turning off compression in new databases
        private bool compressColumns;

        // !EFW - Added support for local cache to speed up read-only operations
        private ConcurrentDictionary<TKey, TValue> localCache;
        private int localCacheSize, localCacheFlushCount;

        // !EFW
        /// <summary>
        /// Set this to a non-zero value to enable local caching of values to speed up read-only access
        /// </summary>
        /// <value>If set to zero, the default, the local cache will not be used and all values will be retrieved
        /// from the database.  This is only intended for read-only dictionaries.  The local cache will not be
        /// updated if the dictionary entries are updated.</value>
        public int LocalCacheSize
        {
            get { return localCacheSize; }
            set
            {
                if(value < 1)
                {
                    localCacheSize = 0;
                    localCache = null;
                }
                else
                {
                    localCacheSize = value;
                    localCache = new ConcurrentDictionary<TKey, TValue>();
                }

                localCacheFlushCount = 0;
            }
        }

        // !EFW
        /// <summary>
        /// This read-only property returns the number of times the local cache was flushed because it filled up
        /// </summary>
        /// <value>This can help in figuring out an appropriate local cache size</value>
        public int LocalCacheFlushCount
        {
            get { return localCacheFlushCount; }
        }

        // !EFW
        /// <summary>
        /// This read-only property returns the current number of local cache entries in use
        /// </summary>
        public int CurrentLocalCacheCount
        {
            get { return (localCache == null) ? 0 : localCache.Count; }
        }

        /// <summary>
        /// Database object associated with the PersistentDictionary.
        /// </summary>
        private readonly Database database;

        /// <summary>
        /// Tracks whether the object has been Disposed.
        /// </summary>
        private bool alreadyDisposed;

        // !EFW - Added support for specifying whether or not to compress columns when constructed
        /// <summary>
        /// Initializes a new instance of the PersistentDictionary class.
        /// </summary>
        /// <param name="directory">
        /// The directory in which to create the database.
        /// </param>
        public PersistentDictionary(string directory) : this(directory, true)
        {
        }

        /// <summary>
        /// Initializes a new instance of the PersistentDictionary class.
        /// </summary>
        /// <param name="directory">
        /// The directory in which to create the database.
        /// </param>
        /// <param name="compressColumns">If true, the column data compression option will be enabled when the
        /// database is created.  If false, the compression option is left off.  This only has an effect when
        /// creating the database.  It will not change the compression option in existing files.</param>
        public PersistentDictionary(string directory, bool compressColumns) : this(directory, null, null, compressColumns)
        {
            if(null == directory)
            {
                throw new ArgumentNullException("directory");
            }
        }


        /// <summary>
        /// Initializes a new instance of the PersistentDictionary class.
        /// </summary>
        /// <param name="customConfig">The custom config to use for creating the PersistentDictionary.</param>
        public PersistentDictionary(IConfigSet customConfig) : this(null, customConfig, null, true)
        {
            if (null == customConfig)
            {
                throw new ArgumentNullException("customConfig");
            }
        }

        /// <summary>
        /// Initializes a new instance of the PersistentDictionary class.
        /// </summary>
        /// <param name="directory">The directory in which to create the database.</param>
        /// <param name="customConfig">The custom config to use for creating the PersistentDictionary.</param>
        public PersistentDictionary(string directory, IConfigSet customConfig) :
            this(directory, customConfig, null, true)
        {
            if (directory == null && customConfig == null)
            {
                throw new ArgumentException("Must specify a valid directory or customConfig");
            }
        }

        /// <summary>
        /// Initializes a new instance of the PersistentDictionary class.
        /// </summary>
        /// <param name="dictionary">
        /// The IDictionary whose contents are copied to the new dictionary.
        /// </param>
        /// <param name="directory">
        /// The directory in which to create the database.
        /// </param>
        public PersistentDictionary(IEnumerable<KeyValuePair<TKey, TValue>> dictionary, string directory) :
            this(directory, null, dictionary, true)
        {
            if (null == directory)
            {
                throw new ArgumentNullException("directory");
            }

            if (null == dictionary)
            {
                this.Dispose();
                throw new ArgumentNullException("dictionary");
            }
        }

        /// <summary>
        /// Initializes a new instance of the PersistentDictionary class.
        /// </summary>
        /// <param name="dictionary">The IDictionary whose contents are copied to the new dictionary.</param>
        /// <param name="customConfig">The custom config to use for creating the PersistentDictionary.</param>
        public PersistentDictionary(IEnumerable<KeyValuePair<TKey, TValue>> dictionary, IConfigSet customConfig) :
            this(null, customConfig, dictionary, true)
        {
            if (null == customConfig)
            {
                throw new ArgumentNullException("customConfig");
            }

            if (null == dictionary)
            {
                this.Dispose();
                throw new ArgumentNullException("dictionary");
            }
        }

        /// <summary>
        /// Initializes a new instance of the PersistentDictionary class.
        /// </summary>
        /// <param name="dictionary">The IDictionary whose contents are copied to the new dictionary.</param>
        /// <param name="directory">The directory in which to create the database.</param>
        /// <param name="customConfig">The custom config to use for creating the PersistentDictionary.</param>
        public PersistentDictionary(
            IEnumerable<KeyValuePair<TKey, TValue>> dictionary,
            string directory,
            IConfigSet customConfig)
            : this(directory, customConfig, dictionary, true)
        {
            if (directory == null && customConfig == null)
            {
                throw new ArgumentException("Must specify a valid directory or customConfig");
            }

            if (null == dictionary)
            {
                this.Dispose();
                throw new ArgumentNullException("dictionary");
            }
        }

        /// <summary>
        /// Initializes a new instance of the PersistentDictionary class.
        /// </summary>
        /// <param name="directory">The directory to create the database in.</param>
        /// <param name="customConfig">The custom config to use for creating the PersistentDictionary.</param>
        /// <param name="dictionary">The IDictionary whose contents are copied to the new dictionary.</param>
        /// <param name="compressColumns">If true, the column data compression option will be enabled when the
        /// database is created.  If false, the compression option is left off.  This only has an effect when
        /// creating the database.  It will not change the compression option in existing files.</param>
        /// <remarks>The constructor can either initialize PersistentDictionary from a directory string, or a full custom config set. But not both.</remarks>
        private PersistentDictionary(
            string directory,
            IConfigSet customConfig,
            IEnumerable<KeyValuePair<TKey, TValue>> dictionary,
            bool compressColumns)
        {
            Contract.Requires(directory != null || customConfig != null); // At least 1 of the two arguments should be set
            if (directory == null && customConfig == null)
            {
                return; // The calling constructor will throw an error
            }

            // !EFW - Set the column compression option
            this.compressColumns = compressColumns;

            this.converters = new PersistentDictionaryConverters<TKey, TValue>();
            this.schema = new PersistentDictionaryConfig();
            var defaultConfig = PersistentDictionaryDefaultConfig.GetDefaultDatabaseConfig();
            var databaseConfig = new DatabaseConfig();

            if (directory != null)
            {
                this.databaseDirectory = directory;
                this.databasePath = Path.Combine(directory, defaultConfig.DatabaseFilename);
                databaseConfig.DatabaseFilename = this.databasePath;
                databaseConfig.SystemPath = this.databaseDirectory;
                databaseConfig.LogFilePath = this.databaseDirectory;
                databaseConfig.TempPath = this.databaseDirectory;

                // If the database has been moved while inconsistent recovery
                // won't be able to find the database (logfiles contain the
                // absolute path of the referenced database). Set this parameter
                // to indicate a directory which contains any databases that couldn't
                // be found by recovery.
                databaseConfig.AlternateDatabaseRecoveryPath = directory;
            }

            if (customConfig != null)
            {
                databaseConfig.Merge(customConfig); // throw on conflicts
            }

            // Use defaults for anything that the caller didn't explicitly set
            databaseConfig.Merge(defaultConfig, MergeRules.KeepExisting);

            // Finally, we know what database path to use
            this.databaseDirectory = Path.GetDirectoryName(databaseConfig.DatabaseFilename);
            this.databasePath = databaseConfig.DatabaseFilename;

            this.updateLocks = new object[NumUpdateLocks];
            for (int i = 0; i < this.updateLocks.Length; ++i)
            {
                this.updateLocks[i] = new object();
            }

            databaseConfig.SetGlobalParams();
            this.instance = new Instance(databaseConfig.Identifier, databaseConfig.DisplayName, databaseConfig.DatabaseStopFlags);

            databaseConfig.SetInstanceParams(this.instance.JetInstance);

            InitGrbit grbit = databaseConfig.DatabaseRecoveryFlags |
                                  (EsentVersion.SupportsWindows7Features ? Windows7Grbits.ReplayIgnoreLostLogs : InitGrbit.None);
            this.instance.Init(grbit);

            try
            {
                if (!File.Exists(this.databasePath))
                {
                    this.CreateDatabase(databaseConfig);
                }
                else
                {
                    this.CheckDatabaseMetaData(databaseConfig);
                }

                this.cursors = new PersistentDictionaryCursorCache<TKey, TValue>(
                    this.instance, this.databasePath, this.converters, this.schema);
            }
            catch (Exception)
            {
                // We have failed to initialize for some reason. Terminate
                // the instance.
                this.instance.Term();
                throw;
            }

            this.database = new Database(this.instance.JetInstance, false, databaseConfig);

            // Optionally, fill the db from the supplied dictionary
            if (dictionary != null)
            {
                try
                {
                    foreach (KeyValuePair<TKey, TValue> item in dictionary)
                    {
                        this.Add(item);
                    }
                }
                catch (Exception)
                {
                    // We have failed to copy the dictionary. Terminate the instance.
                    this.Dispose();
                    throw;
                }
            }
        }

        /// <summary>
        /// Gets the number of elements contained in the <see cref="PersistentDictionary{TKey,TValue}"/>.
        /// </summary>
        /// <value>
        /// The number of elements contained in the <see cref="PersistentDictionary{TKey,TValue}"/>.
        /// </value>
        public int Count
        {
            get
            {
                return this.ReturnReadLockedOperation(
                    () =>
                        {
                            PersistentDictionaryCursor<TKey, TValue> cursor = this.cursors.GetCursor();
                            try
                            {
                                return cursor.RetrieveCount();
                            }
                            finally
                            {
                                this.cursors.FreeCursor(cursor);
                            }
                        });
            }
        }

        /// <summary>
        /// Gets a value indicating whether the <see cref="PersistentDictionary{TKey,TValue}"/> is read-only.
        /// </summary>
        /// <value>
        /// True if the <see cref="PersistentDictionary{TKey,TValue}"/> is read-only; otherwise, false.
        /// </value>
        public bool IsReadOnly
        {
            get
            {
                return false;
            }
        }

        /// <summary>
        /// Gets an <see cref="ICollection"/> containing the keys of the <see cref="PersistentDictionary{TKey,TValue}"/>.
        /// </summary>
        /// <returns>
        /// An <see cref="PersistentDictionaryKeyCollection{TKey,TValue}"/> containing the keys of the object that implements <see cref="PersistentDictionary{TKey,TValue}"/>.
        /// </returns>
        ICollection<TKey> IDictionary<TKey, TValue>.Keys
        {
            get
            {
                return this.Keys;
            }
        }

        /// <summary>
        /// Gets an <see cref="ICollection"/> containing the keys of the <see cref="PersistentDictionary{TKey,TValue}"/>.
        /// </summary>
        /// <value>
        /// An <see cref="PersistentDictionaryKeyCollection{TKey,TValue}"/> containing the keys of the object that implements <see cref="PersistentDictionary{TKey,TValue}"/>.
        /// </value>
        public PersistentDictionaryKeyCollection<TKey, TValue> Keys
        {
            get
            {
                return this.ReturnReadLockedOperation(
                    () =>
                        {
                            return new PersistentDictionaryKeyCollection<TKey, TValue>(this);
                        });
            }
        }

        /// <summary>
        /// Gets an <see cref="ICollection"/> containing the values in the <see cref="PersistentDictionary{TKey,TValue}"/>.
        /// </summary>
        /// <value>
        /// An <see cref="PersistentDictionary{TKey,TValue}"/> containing the values in the object that implements <see cref="PersistentDictionary{TKey,TValue}"/>.
        /// </value>
        ICollection<TValue> IDictionary<TKey, TValue>.Values
        {
            get
            {
                return this.Values;
            }
        }

        /// <summary>
        /// Gets an <see cref="ICollection"/> containing the values in the <see cref="PersistentDictionary{TKey,TValue}"/>.
        /// </summary>
        /// <value>
        /// An <see cref="PersistentDictionary{TKey,TValue}"/> containing the values in the object that implements <see cref="PersistentDictionary{TKey,TValue}"/>.
        /// </value>
        public PersistentDictionaryValueCollection<TKey, TValue> Values
        {
            get
            {
                return this.ReturnReadLockedOperation(
                    () =>
                        {
                            return new PersistentDictionaryValueCollection<TKey, TValue>(this);
                        });
            }
        }

        /// <summary>
        /// Gets the schema configuration used by dictionary to store data in the underlying database.
        /// </summary>
        /// <value>
        /// The schema configuration used by dictionary to store data in the underlying database.
        /// </value>
        public IPersistentDictionaryConfig Schema
        {
            get
            {
                return this.schema;
            }
        }

        /// <summary>
        /// Gets the path of the directory that contains the dictionary database.
        /// The database consists of a set of files found in the directory.
        /// </summary>
        /// <value>
        /// The path of the directory that contains the dictionary database.
        /// </value>
        public string DatabasePath
        {
            get
            {
                return this.databaseDirectory;
            }
        }

        /// <summary>
        /// Gets the Database object associated with the dictionary.
        /// Database can be used to control runtime parameters affecting the dictionary's backing database (e.g. database cache size).
        /// See <see cref="DatabaseConfig"/>.
        /// </summary>
        /// <value>
        /// The Database object associated with the dictionary.
        /// Database can be used to control runtime parameters affecting the dictionary's backing database (e.g. database cache size).
        /// See <see cref="DatabaseConfig"/>.
        /// </value>
        public Database Database
        {
            get
            {
                return this.ReturnReadLockedOperation(
                    () =>
                        {
                            return this.database;
                        });
            }
        }

        /// <summary>
        /// Gets or sets the element with the specified key.
        /// </summary>
        /// <returns>
        /// The element with the specified key.
        /// </returns>
        /// <param name="key">The key of the element to get or set.</param>
        /// <exception cref="T:System.Collections.Generic.KeyNotFoundException">
        /// The property is retrieved and <paramref name="key"/> is not found.
        /// </exception>
        public TValue this[TKey key]
        {
            get
            {
                return this.ReturnReadLockedOperation(
                    () =>
                        {
                            // !EFW - Added support for local caching of values for read-only access
                            if(localCache != null && localCache.TryGetValue(key, out TValue value))
                                return value;

                            PersistentDictionaryCursor<TKey, TValue> cursor = this.cursors.GetCursor();
                            try
                            {
                                using (var transaction = cursor.BeginReadOnlyTransaction())
                                {
                                    cursor.SeekWithKeyNotFoundException(key);
                                    value = cursor.RetrieveCurrentValue();

                                    // !EFW
                                    if(localCache != null)
                                    {
                                        // If the cache is filled, clear it and start over.  Not the most
                                        // sophisticated method, but it works.
                                        if(localCache.Count >= localCacheSize)
                                        {
                                            localCache.Clear();
                                            localCacheFlushCount++;
                                        }

                                        localCache[key] = value;
                                    }

                                    return value;
                                }
                            }
                            finally
                            {
                                this.cursors.FreeCursor(cursor);
                            }
                        });
            }

            set
            {
                this.DoReadLockedOperation(
                    () =>
                        {
                            PersistentDictionaryCursor<TKey, TValue> cursor = this.cursors.GetCursor();
                            cursor.MakeKey(key);
                            lock (this.LockObject(cursor.GetNormalizedKey()))
                            {
                                try
                                {
                                    using (var transaction = cursor.BeginLazyTransaction())
                                    {
                                        if (cursor.TrySeek())
                                        {
                                            cursor.ReplaceCurrentValue(value);
                                        }
                                        else
                                        {
                                            cursor.Insert(new KeyValuePair<TKey, TValue>(key, value));
                                        }

                                        transaction.Commit();
                                    }
                                }
                                finally
                                {
                                    this.cursors.FreeCursor(cursor);
                                }
                            }
                        });
            }
        }

        /// <summary>
        /// Returns an enumerator that iterates through the collection.
        /// </summary>
        /// <returns>
        /// A <see cref="T:System.Collections.Generic.IEnumerator`1"/> 
        /// that can be used to iterate through the collection.
        /// </returns>
        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
        {
            return this.ReturnReadLockedOperation(
                () =>
                {
                    return new PersistentDictionaryEnumerator<TKey, TValue, KeyValuePair<TKey, TValue>>(
                        this, KeyRange<TKey>.OpenRange, c => c.RetrieveCurrent(), x => true);
                });
        }

        /// <summary>
        /// Returns an enumerator that iterates through a collection.
        /// </summary>
        /// <returns>
        /// A <see cref="T:System.Collections.IEnumerator"/>
        /// object that can be used to iterate through the collection.
        /// </returns>
        IEnumerator IEnumerable.GetEnumerator()
        {
            return this.GetEnumerator();
        }

        /// <summary>
        /// Removes the first occurrence of a specific object from the <see cref="PersistentDictionary{TKey,TValue}"/>.
        /// </summary>
        /// <returns>
        /// True if <paramref name="item"/> was successfully removed from the <see cref="PersistentDictionary{TKey,TValue}"/>;
        /// otherwise, false. This method also returns false if <paramref name="item"/> is not found in the original
        /// <see cref="PersistentDictionary{TKey,TValue}"/>.
        /// </returns>
        /// <param name="item">The object to remove from the <see cref="PersistentDictionary{TKey,TValue}"/>.</param>
        public bool Remove(KeyValuePair<TKey, TValue> item)
        {
            return this.ReturnReadLockedOperation(
                () =>
                    {
                        PersistentDictionaryCursor<TKey, TValue> cursor = this.cursors.GetCursor();
                        cursor.MakeKey(item.Key);
                        lock (this.LockObject(cursor.GetNormalizedKey()))
                        {
                            try
                            {
                                // Having the update lock means the record can't be
                                // deleted after we seek to it.
                                if (cursor.TrySeek() && cursor.RetrieveCurrentValue().Equals(item.Value))
                                {
                                    using (var transaction = cursor.BeginLazyTransaction())
                                    {
                                        cursor.DeleteCurrent();
                                        transaction.Commit();
                                        return true;
                                    }
                                }

                                return false;
                            }
                            finally
                            {
                                this.cursors.FreeCursor(cursor);
                            }
                        }
                    });
        }

        /// <summary>
        /// Adds an item to the <see cref="PersistentDictionary{TKey,TValue}"/>.
        /// </summary>
        /// <param name="item">The object to add to the <see cref="PersistentDictionary{TKey,TValue}"/>.</param>
        public void Add(KeyValuePair<TKey, TValue> item)
        {
            this.DoReadLockedOperation(
                () =>
                    {
                        PersistentDictionaryCursor<TKey, TValue> cursor = this.cursors.GetCursor();
                        cursor.MakeKey(item.Key);
                        lock (this.LockObject(cursor.GetNormalizedKey()))
                        {
                            try
                            {
                                using (var transaction = cursor.BeginLazyTransaction())
                                {
                                    if (cursor.TrySeek())
                                    {
                                        throw new ArgumentException("An item with this key already exists", "item");
                                    }

                                    cursor.Insert(item);
                                    transaction.Commit();
                                }
                            }
                            finally
                            {
                                this.cursors.FreeCursor(cursor);
                            }
                        }
                    });
        }

        /// <summary>
        /// Determines whether the <see cref="PersistentDictionary{TKey,TValue}"/> contains a specific value.
        /// </summary>
        /// <returns>
        /// True if <paramref name="item"/> is found in the
        /// <see cref="PersistentDictionary{TKey,TValue}"/>;
        /// otherwise, false.
        /// </returns>
        /// <param name="item">
        /// The object to locate in the <see cref="PersistentDictionary{TKey,TValue}"/>.
        /// </param>
        public bool Contains(KeyValuePair<TKey, TValue> item)
        {
            return this.ReturnReadLockedOperation(
                () =>
                    {
                        PersistentDictionaryCursor<TKey, TValue> cursor = this.cursors.GetCursor();
                        try
                        {
                            // Start a transaction here to avoid the case where the record
                            // is deleted after we seek to it.
                            using (var transaction = cursor.BeginReadOnlyTransaction())
                            {
                                bool isPresent = cursor.TrySeek(item.Key)
                                                 && Compare.AreEqual(item.Value, cursor.RetrieveCurrentValue());
                                return isPresent;
                            }
                        }
                        finally
                        {
                            this.cursors.FreeCursor(cursor);
                        }
                    });
        }

        /// <summary>
        /// Copies the elements of the <see cref="PersistentDictionary{TKey,TValue}"/> to an <see cref="T:System.Array"/>, starting at a particular <see cref="T:System.Array"/> index.
        /// </summary>
        /// <param name="array">
        /// The one-dimensional <see cref="T:System.Array"/> that is the destination
        /// of the elements copied from <see cref="PersistentDictionary{TKey,TValue}"/>.
        /// The <see cref="T:System.Array"/> must have zero-based indexing.</param>
        /// <param name="arrayIndex">
        /// The zero-based index in <paramref name="array"/> at which copying begins.
        /// </param>
        /// <exception cref="T:System.ArgumentNullException"><paramref name="array"/> is null.</exception>
        /// <exception cref="T:System.ArgumentOutOfRangeException"><paramref name="arrayIndex"/> is less than 0.</exception>
        /// <exception cref="T:System.ArgumentException">
        /// <paramref name="arrayIndex"/> is equal to or greater than the length of <paramref name="array"/>.
        /// -or-The number of elements in the source <see cref="PersistentDictionary{TKey,TValue}"/> is greater
        /// than the available space from <paramref name="arrayIndex"/> to the end of the destination <paramref name="array"/>.
        /// </exception>
        public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex)
        {
            this.DoReadLockedOperation(
                () =>
                    {
                        Copy.CopyTo(this, array, arrayIndex);
                    });
        }

        /// <summary>
        /// Removes all items from the <see cref="PersistentDictionary{TKey,TValue}"/>.
        /// </summary>
        public void Clear()
        {
            this.DoReadLockedOperation(
                () =>
                    {
                        try
                        {
                            // We will be deleting all items so take all the update locks
                            foreach (object lockObject in this.updateLocks)
                            {
                                Monitor.Enter(lockObject);
                            }

                            PersistentDictionaryCursor<TKey, TValue> cursor = this.cursors.GetCursor();
                            try
                            {
                                cursor.MoveBeforeFirst();
                                while (cursor.TryMoveNext())
                                {
                                    using (var transaction = cursor.BeginLazyTransaction())
                                    {
                                        cursor.DeleteCurrent();
                                        transaction.Commit();
                                    }
                                }
                            }
                            finally
                            {
                                this.cursors.FreeCursor(cursor);
                            }
                        }
                        finally
                        {
                            // Remember to unlock everything
                            foreach (object lockObject in this.updateLocks)
                            {
                                Monitor.Exit(lockObject);
                            }
                        }
                    });
        }

        /// <summary>
        /// Determines whether the <see cref="PersistentDictionary{TKey,TValue}"/> contains an element with the specified key.
        /// </summary>
        /// <returns>
        /// True if the <see cref="PersistentDictionary{TKey,TValue}"/> contains an element with the key; otherwise, false.
        /// </returns>
        /// <param name="key">The key to locate in the <see cref="PersistentDictionary{TKey,TValue}"/>.</param>
        public bool ContainsKey(TKey key)
        {
            return this.ReturnReadLockedOperation(
                () =>
                    {
                        // !EFW - Added support for local caching of values for read-only access
                        if(localCache != null && localCache.ContainsKey(key))
                            return true;

                        PersistentDictionaryCursor<TKey, TValue> cursor = this.cursors.GetCursor();
                        try
                        {
                            return cursor.TrySeek(key);
                        }
                        finally
                        {
                            this.cursors.FreeCursor(cursor);
                        }
                    });
        }

        /// <summary>
        /// Adds an element with the provided key and value to the <see cref="PersistentDictionary{TKey,TValue}"/>.
        /// </summary>
        /// <param name="key">The object to use as the key of the element to add.</param>
        /// <param name="value">The object to use as the value of the element to add.</param>
        /// <exception cref="T:System.ArgumentException">An element with the same key already exists in the <see cref="PersistentDictionary{TKey,TValue}"/>.</exception>
        public void Add(TKey key, TValue value)
        {
            this.Add(new KeyValuePair<TKey, TValue>(key, value));
        }

        /// <summary>
        /// Removes the element with the specified key from the <see cref="PersistentDictionary{TKey,TValue}"/>.
        /// </summary>
        /// <returns>
        /// True if the element is successfully removed; otherwise, false. This method also returns false if
        /// <paramref name="key"/> was not found in the original <see cref="PersistentDictionary{TKey,TValue}"/>.
        /// </returns>
        /// <param name="key">The key of the element to remove.</param>
        public bool Remove(TKey key)
        {
            return this.ReturnReadLockedOperation(
                () =>
                    {
                        PersistentDictionaryCursor<TKey, TValue> cursor = this.cursors.GetCursor();
                        cursor.MakeKey(key);

                        lock (this.LockObject(cursor.GetNormalizedKey()))
                        {
                            try
                            {
                                if (cursor.TrySeek(key))
                                {
                                    using (var transaction = cursor.BeginLazyTransaction())
                                    {
                                        cursor.DeleteCurrent();
                                        transaction.Commit();
                                        return true;
                                    }
                                }

                                return false;
                            }
                            finally
                            {
                                this.cursors.FreeCursor(cursor);
                            }
                        }
                    });
        }

        /// <summary>
        /// Gets the value associated with the specified key.
        /// </summary>
        /// <returns>
        /// True if the object that implements <see cref="PersistentDictionary{TKey,TValue}"/>
        /// contains an element with the specified key; otherwise, false.
        /// </returns>
        /// <param name="key">
        /// The key whose value to get.</param>
        /// <param name="value">When this method returns, the value associated
        /// with the specified key, if the key is found; otherwise, the default
        /// value for the type of the <paramref name="value"/> parameter. This
        /// parameter is passed uninitialized.
        /// </param>
        public bool TryGetValue(TKey key, out TValue value)
        {
            TValue toReturn = default(TValue);

            bool found = this.ReturnReadLockedOperation(
                () =>
                    {
                        TValue retrievedValue = default;

                        // !EFW - Added support for local caching of values for read-only access
                        if(localCache != null && localCache.TryGetValue(key, out toReturn))
                            return true;

                        PersistentDictionaryCursor<TKey, TValue> cursor = this.cursors.GetCursor();
                        try
                        {
                            // Start a transaction so the record can't be deleted after
                            // we seek to it.
                            bool isPresent = false;
                            using (var transaction = cursor.BeginReadOnlyTransaction())
                            {
                                if (cursor.TrySeek(key))
                                {
                                    retrievedValue = cursor.RetrieveCurrentValue();
                                    isPresent = true;

                                    // !EFW
                                    if(localCache != null)
                                    {
                                        // If the cache is filled, clear it and start over.  Not the most
                                        // sophisticated method, but it works.
                                        if(localCache.Count >= localCacheSize)
                                        {
                                            localCache.Clear();
                                            localCacheFlushCount++;
                                        }

                                        localCache[key] = retrievedValue;
                                    }
                                }
                            }

                            toReturn = retrievedValue;
                            return isPresent;
                        }
                        finally
                        {
                            this.cursors.FreeCursor(cursor);
                        }
                    });

            value = toReturn;
            return found;
        }

        /// <summary>
        /// Determines whether the <see cref="PersistentDictionary{TKey,TValue}"/> contains an element with the specified value.
        /// </summary>
        /// <remarks>
        /// This method requires a complete enumeration of all items in the dictionary so it can be much slower than
        /// <see cref="ContainsKey"/>.
        /// </remarks>
        /// <returns>
        /// True if the <see cref="PersistentDictionary{TKey,TValue}"/> contains an element with the value; otherwise, false.
        /// </returns>
        /// <param name="value">The value to locate in the <see cref="PersistentDictionary{TKey,TValue}"/>.</param>
        public bool ContainsValue(TValue value)
        {
            return this.ReturnReadLockedOperation(
                () =>
                    {
                        return this.Values.Contains(value);
                    });
        }

        /// <summary>
        /// Force all changes made to this dictionary to be written to disk.
        /// </summary>
        public void Flush()
        {
            this.DoReadLockedOperation(
                () =>
                    {
                        PersistentDictionaryCursor<TKey, TValue> cursor = this.cursors.GetCursor();
                        try
                        {
                            cursor.Flush();
                        }
                        finally
                        {
                            this.cursors.FreeCursor(cursor);
                        }
                    });
        }

        /// <summary>
        /// Invokes the Dispose(bool) function.
        /// </summary>
        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Opens a cursor on the PersistentDictionary. Used by enumerators.
        /// </summary>
        /// <returns>
        /// A new cursor that can be used to enumerate the PersistentDictionary.
        /// </returns>
        internal PersistentDictionaryCursor<TKey, TValue> GetCursor()
        {
            return this.ReturnReadLockedOperation(
                () =>
                    {
                        return this.cursors.GetCursor();
                    });
        }

        /// <summary>
        /// Frees a cursor on the PersistentDictionary. Used by enumerators.
        /// </summary>
        /// <param name="cursor">
        /// The cursor being freed.
        /// </param>
        internal void FreeCursor(PersistentDictionaryCursor<TKey, TValue> cursor)
        {
            this.DoReadLockedOperation(
                () =>
                    {
                        this.cursors.FreeCursor(cursor);
                    });
        }

        /// <summary>
        /// Returns an enumerator that iterates through the values.
        /// </summary>
        /// <returns>
        /// A <see cref="T:System.Collections.Generic.IEnumerator`1"/> that can be used to iterate through the values.
        /// </returns>
        internal IEnumerator<TValue> GetValueEnumerator()
        {
            return new PersistentDictionaryEnumerator<TKey, TValue, TValue>(
                this, KeyRange<TKey>.OpenRange, c => c.RetrieveCurrentValue(), x => true);
        }

        /// <summary>
        /// Performs the specified action while under a ReadLock.
        /// </summary>
        /// <param name="action">The action to perform.</param>
        internal void DoReadLockedOperation(Action action)
        {
            this.CheckObjectDisposed();

            try
            {
                this.disposeLock.EnterReadLock();
                this.CheckObjectDisposed();

                action();
            }
            finally
            {
                this.disposeLock.ExitReadLock();
            }
        }

        /// <summary>
        /// Performs the specified action while under a ReadLock. This is usually done to
        /// prevent the underlying dictionary from being Dispose'd from underneath us.
        /// </summary>
        /// <param name="action">The action to perform.</param>
        /// <typeparam name="TReturn">The type of the return value of the block.</typeparam>
        /// <returns>Returns the value of the function.</returns>
        internal TReturn ReturnReadLockedOperation<TReturn>(Func<TReturn> action)
        {
            this.CheckObjectDisposed();

            try
            {
                this.disposeLock.EnterReadLock();
                this.CheckObjectDisposed();

                return action();
            }
            finally
            {
                this.disposeLock.ExitReadLock();
            }
        }

        /// <summary>
        /// Determine if the given column can be compressed.
        /// </summary>
        /// <param name="columndef">The definition of the column.</param>
        /// <returns>True if the column can be compressed.</returns>
        private static bool ColumnCanBeCompressed(JET_COLUMNDEF columndef)
        {
            return EsentVersion.SupportsWindows7Features
                   && (JET_coltyp.LongText == columndef.coltyp || JET_coltyp.LongBinary == columndef.coltyp);
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        /// <param name="userInitiatedDisposing">Whether it's a user-initiated call.</param>
        private void Dispose(bool userInitiatedDisposing)
        {
            if (this.alreadyDisposed)
            {
                return;
            }

            if (userInitiatedDisposing)
            {
                // Indicates a coding error.
                Debug.Assert(!this.disposeLock.IsReadLockHeld, "No read lock should be held when Disposing this object.");
                Debug.Assert(!this.disposeLock.IsWriteLockHeld, "No read lock should be held when Disposing this object.");

                bool writeLocked = false;
                try
                {
                    this.disposeLock.EnterWriteLock();
                    writeLocked = true;

                    if (this.alreadyDisposed)
                    {
                        return;
                    }

                    this.cursors.Dispose();
                    this.database.Dispose();
                    this.instance.Dispose();
                }
                finally
                {
                    this.alreadyDisposed = true;
                    if (writeLocked)
                    {
                        this.disposeLock.ExitWriteLock();
                    }

                    // Can't Dipose it when other threads may be blocked on it,
                    // trying to enter as Readers.
                    //// this.disposeLock.Dispose();
                }
            }
        }

        /// <summary>
        /// Check the database meta-data. This makes sure the tables and columns exist and
        /// checks the type of the database.
        /// </summary>
        /// <param name="databaseConfig">The database configuration to use.</param>
        private void CheckDatabaseMetaData(DatabaseConfig databaseConfig)
        {
            string databasePath = databaseConfig.DatabaseFilename;
            using (var session = new Session(this.instance))
            {
                JET_DBID dbid;
                JET_TABLEID tableid;

                Api.JetAttachDatabase2(session, databasePath, databaseConfig.DatabaseMaxPages, databaseConfig.DatabaseAttachFlags);
                Api.JetOpenDatabase(session, databasePath, string.Empty, out dbid, OpenDatabaseGrbit.None);

                // Globals table
                Api.JetOpenTable(session, dbid, this.schema.GlobalsTableName, null, 0, OpenTableGrbit.None, out tableid);
                Api.GetTableColumnid(session, tableid, this.schema.CountColumnName);
                Api.GetTableColumnid(session, tableid, this.schema.FlushColumnName);
                var keyTypeColumnid = Api.GetTableColumnid(session, tableid, this.schema.KeyTypeColumnName);
                var valueTypeColumnid = Api.GetTableColumnid(session, tableid, this.schema.ValueTypeColumnName);
                if (!Api.TryMoveFirst(session, tableid))
                {
                    throw new InvalidDataException("globals table is empty");
                }

#if ESENTCOLLECTIONS_SUPPORTS_SERIALIZATION
                Type keyType = (Type)Api.DeserializeObjectFromColumn(session, tableid, keyTypeColumnid);
                Type valueType = (Type)Api.DeserializeObjectFromColumn(session, tableid, valueTypeColumnid);
                if (keyType != typeof(TKey) || valueType != typeof(TValue))
                {
                    var error = string.Format(
                        CultureInfo.InvariantCulture,
                        "Database is of type <{0}, {1}>, not <{2}, {3}>",
                        keyType,
                        valueType,
                        typeof(TKey),
                        typeof(TValue));
                    throw new ArgumentException(error);
                }
#endif

                var versionColumnid = Api.GetTableColumnid(session, tableid, this.schema.VersionColumnName);
                bool upgradeNeeded = false;

                JET_COLUMNID keyTypeNameColumnid = JET_COLUMNID.Nil;
                JET_COLUMNID valueTypeNameColumnid = JET_COLUMNID.Nil;

                // Try to get the columns - if they don't exist this is an old version
                // and needs an upgrade
                try
                {
                    keyTypeNameColumnid = Api.GetTableColumnid(session, tableid, this.schema.KeyTypeNameColumnName);
                    valueTypeNameColumnid = Api.GetTableColumnid(session, tableid, this.schema.ValueTypeNameColumnName);
                }
                catch (EsentColumnNotFoundException)
                {
                    upgradeNeeded = true;
                }

                if (upgradeNeeded)
                {
                    Api.JetAddColumn(
                        session,
                        tableid,
                        this.schema.KeyTypeNameColumnName,
                        new JET_COLUMNDEF { coltyp = JET_coltyp.LongText },
                        null,
                        0,
                        out keyTypeNameColumnid);

                    Api.JetAddColumn(
                        session,
                        tableid,
                        this.schema.ValueTypeNameColumnName,
                        new JET_COLUMNDEF { coltyp = JET_coltyp.LongText },
                        null,
                        0,
                        out valueTypeNameColumnid);

                    // Re-establish currency
                    Api.TryMoveFirst(session, tableid);

                    using (var transaction = new Transaction(session))
                    using (var update = new Update(session, tableid, JET_prep.Replace))
                    {
                        Api.SetColumn(session, tableid, versionColumnid, this.schema.Version, Encoding.Unicode);
                        Api.SetColumn(session, tableid, keyTypeNameColumnid, typeof(TKey).ToString(), Encoding.Unicode);
                        Api.SetColumn(session, tableid, valueTypeNameColumnid, typeof(TValue).ToString(), Encoding.Unicode);

                        update.Save();
                        transaction.Commit(CommitTransactionGrbit.None);
                    }
                }
                else
                {
                    string keyTypeName = Api.RetrieveColumnAsString(session, tableid, keyTypeNameColumnid);
                    string valueTypeName = Api.RetrieveColumnAsString(session, tableid, valueTypeNameColumnid);

                    if (keyTypeName != typeof(TKey).ToString() || valueTypeName != typeof(TValue).ToString())
                    {
                        var error = string.Format(
                            CultureInfo.InvariantCulture,
                            "Database is of type <{0}, {1}>, not <{2}, {3}>",
                            keyTypeName,
                            valueTypeName,
                            typeof(TKey).ToString(),
                            typeof(TValue).ToString());
                        throw new ArgumentException(error);
                    }
                }

                Api.JetCloseTable(session, tableid);

                // Data table
                Api.JetOpenTable(session, dbid, this.schema.DataTableName, null, 0, OpenTableGrbit.None, out tableid);
                Api.GetTableColumnid(session, tableid, this.schema.KeyColumnName);
                Api.GetTableColumnid(session, tableid, this.schema.ValueColumnName);
                Api.JetCloseTable(session, tableid);
            }
        }

        /// <summary>
        /// Create the database.
        /// </summary>
        /// <param name="databaseConfig">The database configuration to use.</param>
        private void CreateDatabase(DatabaseConfig databaseConfig)
        {
            string databasePath = databaseConfig.DatabaseFilename;
            using (var session = new Session(this.instance))
            {
                JET_DBID dbid;
                Api.JetCreateDatabase2(session, databasePath, databaseConfig.DatabaseMaxPages, out dbid, databaseConfig.DatabaseCreationFlags);
                try
                {
                    using (var transaction = new Transaction(session))
                    {
                        this.CreateGlobalsTable(session, dbid);
                        this.CreateDataTable(session, dbid);
                        transaction.Commit(CommitTransactionGrbit.None);
                        Api.JetCloseDatabase(session, dbid, CloseDatabaseGrbit.None);
                    }
                }
                catch
                {
                    // Delete the partially constructed database
                    Api.JetCloseDatabase(session, dbid, CloseDatabaseGrbit.None);
                    Api.JetDetachDatabase(session, databasePath);
                    File.Delete(databasePath);
                    throw;
                }
            }
        }

        /// <summary>
        /// Create the globals table.
        /// </summary>
        /// <param name="session">The session to use.</param>
        /// <param name="dbid">The database to create the table in.</param>
        private void CreateGlobalsTable(Session session, JET_DBID dbid)
        {
            JET_TABLEID tableid;
            JET_COLUMNID versionColumnid;
            JET_COLUMNID countColumnid;
            JET_COLUMNID keyTypeColumnid;
            JET_COLUMNID valueTypeColumnid;
            JET_COLUMNID keyTypeNameColumnid = JET_COLUMNID.Nil;
            JET_COLUMNID valueTypeNameColumnid = JET_COLUMNID.Nil;

            Api.JetCreateTable(session, dbid, this.schema.GlobalsTableName, 1, 100, out tableid);
            Api.JetAddColumn(
                session,
                tableid,
                this.schema.VersionColumnName,
                new JET_COLUMNDEF { coltyp = JET_coltyp.LongText },
                null,
                0,
                out versionColumnid);

            byte[] defaultValue = BitConverter.GetBytes(0);

            Api.JetAddColumn(
                session,
                tableid,
                this.schema.CountColumnName,
                new JET_COLUMNDEF { coltyp = JET_coltyp.Long, grbit = ColumndefGrbit.ColumnEscrowUpdate },
                defaultValue,
                defaultValue.Length,
                out countColumnid);

            Api.JetAddColumn(
                session,
                tableid,
                this.schema.FlushColumnName,
                new JET_COLUMNDEF { coltyp = JET_coltyp.Long, grbit = ColumndefGrbit.ColumnEscrowUpdate },
                defaultValue,
                defaultValue.Length,
                out countColumnid);

            Api.JetAddColumn(
                session,
                tableid,
                this.schema.KeyTypeColumnName,
                new JET_COLUMNDEF { coltyp = JET_coltyp.LongBinary },
                null,
                0,
                out keyTypeColumnid);

            Api.JetAddColumn(
                session,
                tableid,
                this.schema.ValueTypeColumnName,
                new JET_COLUMNDEF { coltyp = JET_coltyp.LongBinary },
                null,
                0,
                out valueTypeColumnid);

            Api.JetAddColumn(
                session,
                tableid,
                this.schema.KeyTypeNameColumnName,
                new JET_COLUMNDEF { coltyp = JET_coltyp.LongText },
                null,
                0,
                out keyTypeNameColumnid);

            Api.JetAddColumn(
                session,
                tableid,
                this.schema.ValueTypeNameColumnName,
                new JET_COLUMNDEF { coltyp = JET_coltyp.LongText },
                null,
                0,
                out valueTypeNameColumnid);

            using (var update = new Update(session, tableid, JET_prep.Insert))
            {
#if ESENTCOLLECTIONS_SUPPORTS_SERIALIZATION
                Api.SerializeObjectToColumn(session, tableid, keyTypeColumnid, typeof(TKey));
                Api.SerializeObjectToColumn(session, tableid, valueTypeColumnid, typeof(TValue));
#endif
                Api.SetColumn(session, tableid, versionColumnid, this.schema.Version, Encoding.Unicode);
                Api.SetColumn(session, tableid, keyTypeNameColumnid, typeof(TKey).ToString(), Encoding.Unicode);
                Api.SetColumn(session, tableid, valueTypeNameColumnid, typeof(TValue).ToString(), Encoding.Unicode);

                update.Save();
            }

            Api.JetCloseTable(session, tableid);
        }

        /// <summary>
        /// Create the data table.
        /// </summary>
        /// <param name="session">The session to use.</param>
        /// <param name="dbid">The database to create the table in.</param>
        private void CreateDataTable(Session session, JET_DBID dbid)
        {
            JET_TABLEID tableid;
            JET_COLUMNID keyColumnid;
            JET_COLUMNID valueColumnid;

            Api.JetCreateTable(session, dbid, this.schema.DataTableName, 128, 100, out tableid);
            var columndef = new JET_COLUMNDEF { coltyp = this.converters.KeyColtyp, cp = JET_CP.Unicode, grbit = ColumndefGrbit.None };

            // !EFW - Only compress columns if wanted
            if (compressColumns && ColumnCanBeCompressed(columndef))
            {
                columndef.grbit |= Windows7Grbits.ColumnCompressed;
            }

            Api.JetAddColumn(
                session,
                tableid, 
                this.schema.KeyColumnName,
                columndef,
                null,
                0,
                out keyColumnid);

            columndef = new JET_COLUMNDEF { coltyp = this.converters.ValueColtyp, cp = JET_CP.Unicode, grbit = ColumndefGrbit.None };

            // !EFW - Only compress columns if wanted
            if(compressColumns && ColumnCanBeCompressed(columndef))
            {
                columndef.grbit |= Windows7Grbits.ColumnCompressed;
            }

            Api.JetAddColumn(
                session,
                tableid,
                this.schema.ValueColumnName,
                columndef,
                null,
                0,
                out valueColumnid);

            string indexKey = string.Format(CultureInfo.InvariantCulture, "+{0}\0\0", this.schema.KeyColumnName);
            var indexcreates = new[]
                                   {
                                       new JET_INDEXCREATE
                                           {
                                               cbKeyMost = SystemParameters.KeyMost,
                                               grbit = CreateIndexGrbit.IndexPrimary,
                                               szIndexName = "primary",
                                               szKey = indexKey,
                                               cbKey = indexKey.Length,
                                               pidxUnicode = new JET_UNICODEINDEX
                                                       {
                                                           lcid = CultureInfo.CurrentCulture.LCID,
                                                           dwMapFlags = Conversions.LCMapFlagsFromCompareOptions(CompareOptions.None),
                                                       },
                                           },
                                   };
            Api.JetCreateIndex2(session, tableid, indexcreates, indexcreates.Length);

            Api.JetCloseTable(session, tableid);
        }

        /// <summary>
        /// Verifies that the object is not already disposed.
        /// This should be checked while the disposeLock ReadLock is held. If
        /// the read lock is not held, then the caller must acquire the lock
        /// first and check for a guaranteed correct value.
        /// (The caller may call this without the lock first to get a fast-but-
        /// inaccurate result).
        /// </summary>
        /// <exception cref="ObjectDisposedException">
        /// Thrown if the object has already been disposed.
        /// </exception>
        private void CheckObjectDisposed()
        {
            if (this.alreadyDisposed)
            {
                throw new ObjectDisposedException("PersistentDictionary");
            }
        }

        /// <summary>
        /// Gets an object used to lock updates to the key.
        /// </summary>
        /// <param name="normalizedKey">The normalized key to be locked.</param>
        /// <returns>
        /// An object that should be locked when the key is updated.
        /// </returns>
        private object LockObject(byte[] normalizedKey)
        {
            if (null == normalizedKey)
            {
                return this.updateLocks[0];
            }

            // Remember: hash codes can be negative, and we can't negate Int32.MinValue.
            uint hash = unchecked((uint)PersistentDictionaryMath.GetHashCodeForKey(normalizedKey));
            hash %= checked((uint)this.updateLocks.Length);

            return this.updateLocks[checked((int)hash)];
        }
    }
}
