﻿using Microsoft.ServiceFabric.Data;
using System;
using System.Fabric;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using Trinity.Diagnostics;
using Trinity.DynamicCluster.Consensus;
using Trinity.DynamicCluster.Persistency;
using Trinity.Utilities;

namespace Trinity.ServiceFabric.GarphEngine.Infrastructure
{
    public class RestoreEventArgs : EventArgs
    {
        private TaskCompletionSource<bool> m_src = new TaskCompletionSource<bool>();
        internal RestoreContext m_rctx;

        public Task Wait()
        {
            return m_src.Task;
        }

        internal void Complete(Exception exception = null)
        {
            if (exception == null)
            {
                m_src.SetResult(true);
            }
            else
            {
                m_src.SetException(exception);
            }
        }

        public RestoreEventArgs(RestoreContext restoreContext, CancellationToken cancellationToken)
        {
            m_rctx = restoreContext;
            cancellationToken.Register(() => m_src.SetCanceled());
        }
    }
    class ServiceFabricBackupManager : IBackupManager
    {
        private CancellationToken m_cancel;
        private Task m_init;
        private GraphEngineStatefulServiceRuntime m_svc;

        //SF does not request backups
        public event EventHandler RequestBackup = delegate{ };
        public event EventHandler RequestRestore = delegate{ };

        public void Dispose() { }

        public void Start(CancellationToken cancellationToken)
        {
            m_cancel = cancellationToken;
            m_init   = InitAsync();
        }

        private async Task InitAsync()
        {
            while ((m_svc = GraphEngineStatefulServiceRuntime.Instance) == null)
            {
                await Task.Delay(1000, m_cancel);
            }
            m_svc.RequestRestore += (o, e) => RequestRestore(o, e);
        }

        public async Task Backup(IPersistentUploader uploader, EventArgs _)
        {
            await m_init;
            Log.WriteLine($"{nameof(ServiceFabricBackupManager)}: Creating ServiceFabric backup data.");
            var dsc = new BackupDescription(async (bi, ct) =>
            {
                var fname = Path.Combine(TrinityConfig.StorageRoot, Path.GetRandomFileName());
                Log.WriteLine($"{nameof(ServiceFabricBackupManager)}: Compressing ServiceFabric backup data.");
                ZipFile.CreateFromDirectory(bi.Directory, fname);
                using(var f = File.OpenRead(fname))
                {
                    Log.WriteLine($"{nameof(ServiceFabricBackupManager)}: Uploading ServiceFabric backup data.");
                    await uploader.UploadMetadataAsync(MetadataKey, f);
                }
                Log.WriteLine($"{nameof(ServiceFabricBackupManager)}: Backed up ServiceFabric backup data.");
                return true;
            });
            await m_svc.Backup(dsc);
        }

        public async Task Restore(IPersistentDownloader downloader, EventArgs eventArgs)
        {
            await m_init;
            if (eventArgs == EventArgs.Empty)
            {
                Log.WriteLine($"{nameof(ServiceFabricBackupManager)}: initiating a new restore operation.");
                await m_svc.FabricClient.TestManager.StartPartitionDataLossAsync(Guid.NewGuid(), PartitionSelector.PartitionIdOf(m_svc.Context.ServiceName, m_svc.Context.PartitionId), DataLossMode.PartialDataLoss);
                return;
            }
            if (!(eventArgs is RestoreEventArgs rstArgs))
            {
                // TODO implement active state restore.
                throw new NotSupportedException();
            }
            try
            {
                var ctx = rstArgs.m_rctx;
                var dir = Path.Combine(TrinityConfig.StorageRoot, Path.GetRandomFileName());
                var dsc = new RestoreDescription(dir);
                var fname = Path.Combine(TrinityConfig.StorageRoot, Path.GetRandomFileName());
                using (var file = File.OpenWrite(fname))
                {
                    Log.WriteLine($"{nameof(ServiceFabricBackupManager)}: Downloading ServiceFabric backup data.");
                    await downloader.DownloadMetadataAsync(MetadataKey, file);
                }
                Log.WriteLine($"{nameof(ServiceFabricBackupManager)}: Decompressing ServiceFabric backup data.");
                FileUtility.CompletePath(dir, create_nonexistent: true);
                ZipFile.ExtractToDirectory(fname, dir);
                File.Delete(fname);
                Log.WriteLine($"{nameof(ServiceFabricBackupManager)}: Restoring ServiceFabric backup data.");
                await ctx.RestoreAsync(dsc);
                Directory.Delete(dir, recursive: true);
                Log.WriteLine($"{nameof(ServiceFabricBackupManager)}: Restored ServiceFabric backup data.");
                rstArgs.Complete();
            }
            catch (Exception ex)
            {
                rstArgs.Complete(ex);
                throw;
            }
        }

        private string MetadataKey => $"P{m_svc.PartitionId}_SFBackup.zip";
    }
}
