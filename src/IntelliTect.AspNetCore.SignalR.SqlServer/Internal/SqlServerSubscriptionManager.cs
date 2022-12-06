// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using Microsoft.AspNetCore.SignalR;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace IntelliTect.AspNetCore.SignalR.SqlServer.Internal
{
    internal class SqlServerSubscriptionManager
    {
        private readonly ConcurrentDictionary<string, HubConnectionStore> _subscriptions = new ConcurrentDictionary<string, HubConnectionStore>(StringComparer.Ordinal);
        private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);

        public HubConnectionStore? Get(string id)
        {
            if (id is null) return null;
            return _subscriptions.TryGetValue(id, out var ret) ? ret : null;
        }

        public async Task AddSubscriptionAsync(string id, HubConnectionContext connection)
        {
            await _lock.WaitAsync();

            try
            {
                var subscription = _subscriptions.GetOrAdd(id, _ => new HubConnectionStore());

                subscription.Add(connection);
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task RemoveSubscriptionAsync(string id, HubConnectionContext connection)
        {
            await _lock.WaitAsync();

            try
            {
                if (!_subscriptions.TryGetValue(id, out var subscription))
                {
                    return;
                }

                subscription.Remove(connection);
            }
            finally
            {
                _lock.Release();
            }
        }
    }
}
