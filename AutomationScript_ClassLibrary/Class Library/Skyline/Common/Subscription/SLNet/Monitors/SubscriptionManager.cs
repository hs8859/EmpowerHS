﻿namespace Skyline.DataMiner.Library.Common.Subscription.Monitors
{
    using Skyline.DataMiner.Library.Common.SLNetHelper;

    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Threading;

    internal static class SubscriptionManager
    {
        private static readonly AtomicDictionary<List<SLNetWaitHandle>> subsPerSource = new AtomicDictionary<List<SLNetWaitHandle>>();
        private static readonly ConcurrentDictionary<string, CancellationTokenSource> cleanUpCancelationTokens = new ConcurrentDictionary<string, CancellationTokenSource>();
        private static readonly ConcurrentDictionary<string, MonitorCleanupConfig> monitorCleanupConfig = new ConcurrentDictionary<string, MonitorCleanupConfig>();

        public static bool EmptyHandlerExists { get; set; }

        internal static void SetupMonitorCleanupConfiguration(string sourceIdentifier, MonitorCleanupConfig cleanupConfig)
        {
            if (cleanupConfig == null)
            {
                throw new ArgumentNullException(nameof(cleanupConfig));
            }

            monitorCleanupConfig.AddOrUpdate(sourceIdentifier, cleanupConfig, (key, oldValue) => cleanupConfig);
        }

        internal static int CountCurrentSubscriptions()
        {
            int totalSubs = 0;
            foreach (var key in subsPerSource.GetKeys())
            {
                subsPerSource.GetAndAction(key, (handle) => { totalSubs += handle.Count; });
            }

            return totalSubs;
        }

        internal static void CreateSubscription(string sourceIdentifier, ICommunication connection, SLNetWaitHandle handle, bool overwrite)
        {
            Logger.Log("SubscriptionManager: Creating Subscription: " + handle.SetId);
            subsPerSource.GetOrAddAndAction(sourceIdentifier, new List<SLNetWaitHandle>(), (myHandles) =>
            {
                if (!myHandles.Contains(handle))
                {
                    myHandles.Add(handle);
                    connection.AddSubscriptions(handle.Handler, handle.SetId, handle.Subscriptions);
                }
                else
                {
                    if (overwrite)
                    {
                        var currentHandle = myHandles.First(p => p.SetId == handle.SetId);
                        CleanSubscription(connection, currentHandle);

                        myHandles.Remove(currentHandle);

                        myHandles.Add(handle);
                        connection.AddSubscriptions(handle.Handler, handle.SetId, handle.Subscriptions);
                    }
                }
            });
        }

        internal static void CreateSubscription(string sourceIdentifier, ICommunication connection, SLNetWaitHandle handle, bool overwrite, CancellationTokenSource cts)
        {
            Logger.Log("SubscriptionManager: Creating Subscription: " + handle.SetId);
            subsPerSource.GetOrAddAndAction(sourceIdentifier, new List<SLNetWaitHandle>(), (myHandles) =>
            {
                if (!myHandles.Contains(handle))
                {
                    myHandles.Add(handle);
                    connection.AddSubscriptions(handle.Handler, handle.SetId, handle.Subscriptions);
                }
                else
                {
                    if (overwrite)
                    {
                        var currentHandle = myHandles.First(p => p.SetId == handle.SetId);
                        CleanSubscription(connection, currentHandle);

                        myHandles.Remove(currentHandle);

                        myHandles.Add(handle);
                        connection.AddSubscriptions(handle.Handler, handle.SetId, handle.Subscriptions);
                    }
                }
            });
        }


        internal static void RemoveSubscription(string sourceIdentifier, ICommunication connection, SLNetWaitHandle handle, bool force = false)
        {
            System.Diagnostics.Debug.WriteLine("SubscriptionManager: Removing Subscription: " + handle.SetId);
            Logger.Log("SubscriptionManager: Removing Subscription: " + handle.SetId);
            bool foundHandle = false;

            subsPerSource.GetAndAction(sourceIdentifier, (myHandles) =>
            {
                var cachedHandle = myHandles.FirstOrDefault(p => p.SetId == handle.SetId);
                if (myHandles.Remove(handle))
                {
                    foundHandle = true;
                    CleanSubscription(connection, cachedHandle);
                }
            });

            if (!foundHandle && !force)
            {
                throw new InvalidOperationException("Could not stop subscription: " + handle.SetId);
            }
        }

        /// <summary>
        /// Removes all subscriptions from the source element that involve the destination element.
        /// </summary>
        /// <param name="sourceIdentifier">The identifier of the source element.</param>
        /// <param name="destinationIdentifier">The identifier of the destination element.</param>
        /// <param name="connection">The connection.</param>
        internal static void RemoveSubscriptions(string sourceIdentifier, string destinationIdentifier, ICommunication connection)
        {
            Logger.Log("SubscriptionManager: Removing Subscriptions from destination: " + destinationIdentifier);

            subsPerSource.GetAndAction(sourceIdentifier, (myHandles) =>
            {
                for (int i = myHandles.Count - 1; i >= 0; i--)
                {
                    var handle = myHandles[i];
                    Debug.WriteLine("Handle set ID: " + handle.SetId);

                    if (handle.Destination != null && handle.Destination == destinationIdentifier)
                    {
                        Debug.WriteLine("Removing subscription: " + handle.SetId);
                        CleanSubscription(connection, handle);

                        myHandles.Remove(handle);
                    }
                }

                TryRemoveSourceElementCleanup(sourceIdentifier, connection, myHandles);
            });
        }

        internal static void RemoveSubscriptions(string sourceIdentifier, ICommunication connection)
        {
            try
            {
                Logger.Log("SubscriptionManager: Removing Subscriptions from source: " + sourceIdentifier);
                subsPerSource.ActionAndRemove(sourceIdentifier, (myHandles) =>
                {
                    foreach (var handle in myHandles)
                    {
                        CleanSubscription(connection, handle);
                    }

                    myHandles.Clear();
                });
            }
            finally
            {
                monitorCleanupConfig.TryRemove(sourceIdentifier, out MonitorCleanupConfig config);
            }
        }

        internal static bool ReplaceIfDifferentCachedData(string sourceIdentifier, string handleGuid, string dataKey, object data)
        {
            var result = false;

            subsPerSource.GetAndAction(sourceIdentifier, (myHandles) =>
            {
                System.Diagnostics.Debug.WriteLine("New Data: " + data);
                var myHandle = myHandles.FirstOrDefault(p => p.SetId == handleGuid);
                if (myHandle == null) return;

                object currentData;
                if (myHandle.CachedData.TryGetValue(dataKey, out currentData))
                {
                    System.Diagnostics.Debug.WriteLine("Found existing data");
                    if (!currentData.Equals(data))
                    {
                        System.Diagnostics.Debug.WriteLine("Existing data does not equal new data. Replacing...");
                        myHandle.CachedData[dataKey] = data;
                        result = true;
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("Existing data is same as previous. Not triggering...");
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("No existing data, adding...");
                    myHandle.CachedData[dataKey] = data;
                    result = true;
                }
            });

            return result;
        }

        internal static void TryRemoveCleanupSubs(string sourceIdentifier, ICommunication connection)
        {
            Logger.Log("SubscriptionManager: Removing Self-Cleanup Subscriptions from source: " + sourceIdentifier);
            Dictionary<string, List<SLNetWaitHandle>> subsPerDestination = FindSubsPerDestination(sourceIdentifier);

            var normalSubscriptionOnSourceFound = false;
            foreach (var destinationSubs in subsPerDestination)
            {
                if (destinationSubs.Value.FirstOrDefault(p => p.Type == WaitHandleType.Normal) == null)
                {
                    foreach (var destinationSub in destinationSubs.Value)
                    {
                        SubscriptionManager.RemoveSubscription(sourceIdentifier, connection, destinationSub);
                    }
                }
                else
                {
                    normalSubscriptionOnSourceFound = true;
                }
            }

            if (!normalSubscriptionOnSourceFound)
            {
                SubscriptionManager.RemoveSubscriptions(sourceIdentifier, connection);
            }
        }

        internal static CancellationTokenSource CreateAndSaveCleanupCancelationToken(string setId)
        {
            Logger.Log("SubscriptionManager: Adding cancelation token for set id: " + setId);
            var cancelationToken = new CancellationTokenSource();
            cleanUpCancelationTokens.TryAdd(setId, cancelationToken);
            return cancelationToken;
        }

        internal static void CancelCleanupCancelationToken(string setId)
        {
            Logger.Log("SubscriptionManager: Canceling cancelation token for set id: " + setId);
            CancellationTokenSource cancelationToken;
            if (cleanUpCancelationTokens.TryGetValue(setId, out cancelationToken))
            {
                cancelationToken.Cancel();
            }
        }

        internal static void RemoveCleanupCancelationToken(string setId)
        {
            Logger.Log("SubscriptionManager: Removing cancelation token for set id: " + setId);
            CancellationTokenSource cancelationTokenAux;
            if (cleanUpCancelationTokens.TryRemove(setId, out cancelationTokenAux))
            {
                cancelationTokenAux.Dispose();
            }
        }

        internal static MonitorCleanupConfig TryGetMonitorCleanupConfig(string sourceIdentifier)
        {
            MonitorCleanupConfig config;
            if (monitorCleanupConfig.TryGetValue(sourceIdentifier, out config) && config != null)
            {
                return config;
            }
            else
            {
                return new MonitorCleanupConfig();
            }
        }

        private static void CleanSubscription(ICommunication connection, SLNetWaitHandle handle)
        {
            if (!EmptyHandlerExists)
            {
                System.Diagnostics.Debug.WriteLine("Added Empty Handler");
            }

            connection.ClearSubscriptions(handle.Handler, handle.SetId);//, !EmptyHandlerExists);
            EmptyHandlerExists = true;

            System.Diagnostics.Debug.WriteLine("Removed Subscriptions:" + handle.SetId);
            Logger.Log("SubscriptionManager: Removed " + handle.SetId);
        }

        private static Dictionary<string, List<SLNetWaitHandle>> FindSubsPerDestination(string sourceIdentifier)
        {
            var subsPerDestination = new Dictionary<string, List<SLNetWaitHandle>>();

            subsPerSource.GetAndAction(sourceIdentifier, (remainingSubs) =>
            {
                foreach (var remainingSub in remainingSubs)
                {
                    List<SLNetWaitHandle> handles;
                    if (remainingSub.Destination != null)
                    {
                        if (!subsPerDestination.TryGetValue(remainingSub.Destination, out handles))
                        {
                            handles = new List<SLNetWaitHandle>();
                            subsPerDestination.Add(remainingSub.Destination, handles);
                        }

                        handles.Add(remainingSub);
                    }
                }
            });

            return subsPerDestination;
        }

        private static void TryRemoveSourceElementCleanup(string sourceIdentifier, ICommunication connection, List<SLNetWaitHandle> myHandles)
        {
            if (myHandles.Count > 0)
            {
                var managementHandlersOnly = myHandles.FirstOrDefault(p => p.Type == WaitHandleType.Normal) == null;

                // Clear management subscriptions as there are no real subscriptions left.
                if (managementHandlersOnly)
                {
                    foreach (var handle in myHandles)
                    {
                        CleanSubscription(connection, handle);
                    }

                    myHandles.Clear();

                    // Remove entry from dictionary.
                    Debug.WriteLine("Removing '" + sourceIdentifier + "' entry from subscriptions.");
                    subsPerSource.Remove(sourceIdentifier);
                }
            }
        }

    }
}