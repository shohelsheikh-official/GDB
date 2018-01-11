﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Trinity.Diagnostics;
using Trinity.DynamicCluster.Consensus;
using Trinity.DynamicCluster.Storage;
using Trinity.DynamicCluster.Tasks;
using Trinity.Storage;

namespace Trinity.DynamicCluster.Replication
{
    /// <summary>
    /// Partitioner handles replica information consumption,
    /// data replication and chunk management.
    /// </summary>
    internal class Partitioner : IDisposable
    {
        private static Dictionary<ReplicationMode, IReplicationPlanner> s_planners = new Dictionary<ReplicationMode, IReplicationPlanner>
        {
            { ReplicationMode.Mirroring, new MirroringPlanner() },
            { ReplicationMode.Sharding, new ShardingPlanner() },
            { ReplicationMode.MirroredSharding, new MirrorShardingPlanner() },
            { ReplicationMode.Unrestricted, new UnrestrictedPlanner() },
        };

        private CancellationToken m_cancel;
        private CloudIndex        m_idx;
        private ITaskQueue        m_taskqueue;
        private INameService      m_namesvc;
        private ReplicationMode   m_repmode;
        private int               m_minreplicas;
        private Task              m_partitionerproc;

        public Partitioner(CancellationToken token, CloudIndex idx, INameService namesvc, ITaskQueue taskqueue, ReplicationMode replicationMode, int minimalRedundancy)
        {
            Log.WriteLine($"{nameof(Partitioner)}: Initializing. ReplicationMode={replicationMode}, MinimumReplica={minimalRedundancy}");

            m_cancel          = token;
            m_idx             = idx;
            m_namesvc         = namesvc;
            m_taskqueue       = taskqueue;
            m_repmode         = replicationMode;
            m_minreplicas     = minimalRedundancy;
            m_partitionerproc = Utils.Daemon(m_cancel, "PartitionerProc", 10000, PartitionerProc);
        }

        /// <summary>
        /// PartitionerProc runs on the leader of each partition.
        /// </summary>
        private async Task PartitionerProc()
        {
            if (!m_namesvc.IsMaster) return;
            await Task.WhenAll(
                m_taskqueue.Wait(ReplicatorTask.Guid),
                m_taskqueue.Wait(ShrinkDataTask.Guid),
                m_taskqueue.Wait(PersistedSaveTask.Guid));

            var replica_chunks = await m_idx.GetMyPartitionReplicaChunks();
            if (replica_chunks.Count(p => p.cks.Any()) < m_minreplicas)
            {
                Log.WriteLine($"{nameof(PartitionerProc)}: waiting for {m_minreplicas} ready replicas to conduct partitioning");
                return;
            }

            IEnumerable<ITask> plan = s_planners[m_repmode].Plan(m_minreplicas, replica_chunks);
            // Replication tasks can be done in parallel, and shrink tasks too.
            // However, together they form a multi-stage task -- no shrink task should happen
            // before all rep tasks are done.
            var rep_tasks = plan.OfType<ReplicatorTask>();
            var shr_tasks = plan.OfType<ShrinkDataTask>();
            var chain = new List<ITask>();
            if(rep_tasks.Any()) chain.Add(new GroupedTask(rep_tasks, ReplicatorTask.Guid));
            if(shr_tasks.Any()) chain.Add(new GroupedTask(shr_tasks, ShrinkDataTask.Guid));
            if (!chain.Any()) return;
            var ctask = new ChainedTasks(chain, ReplicatorTask.Guid);
            await m_taskqueue.PostTask(ctask);

            //TODO load balance
        }

        public void Dispose()
        {
            m_partitionerproc.Wait();
        }
    }
}
