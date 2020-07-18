//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.State.Proofs;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Reporting;
using Nethermind.Synchronization.SyncLimits;

namespace Nethermind.Synchronization.FastBlocks
{
    public class SimpleBodiesSyncFeed : ActivatedSyncFeed<SimpleBodiesSyncBatch>
    {
        private int _requestSize = GethSyncLimits.MaxBodyFetch;

        private readonly ILogger _logger;
        private readonly IBlockTree _blockTree;
        private readonly ISyncConfig _syncConfig;
        private readonly ISyncReport _syncReport;
        private readonly ISyncPeerPool _syncPeerPool;

        private readonly long _pivotNumber;

        private FastStatusList _fastStatusList;

        public SimpleBodiesSyncFeed(
            ISyncModeSelector syncModeSelector,
            IBlockTree blockTree,
            ISyncPeerPool syncPeerPool,
            ISyncConfig syncConfig,
            ISyncReport syncReport,
            ILogManager logManager) : base(syncModeSelector, logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _syncPeerPool = syncPeerPool ?? throw new ArgumentNullException(nameof(syncPeerPool));
            _syncConfig = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig));
            _syncReport = syncReport ?? throw new ArgumentNullException(nameof(syncReport));

            if (!_syncConfig.FastBlocks)
            {
                throw new InvalidOperationException("Entered fast blocks mode without fast blocks enabled in configuration.");
            }

            _pivotNumber = _syncConfig.PivotNumberParsed;
            _fastStatusList = new FastStatusList(_blockTree, _pivotNumber);
        }

        protected override SyncMode ActivationSyncModes { get; } = SyncMode.FastBodies & ~SyncMode.FastBlocks;

        public override bool IsMultiFeed => true;

        public override AllocationContexts Contexts => AllocationContexts.Bodies;

        private bool ShouldBuildANewBatch()
        {
            bool shouldDownloadBodies = _syncConfig.DownloadBodiesInFastSync;
            bool allBodiesDownloaded = _fastStatusList.LowestInsertWithoutGaps == 1;
            bool shouldFinish = !shouldDownloadBodies || allBodiesDownloaded;
            if (shouldFinish)
            {
                Finish();
                PostFinishCleanUp();

                return false;
            }

            return true;
        }

        private void PostFinishCleanUp()
        {
            _syncReport.FastBlocksBodies.Update(_pivotNumber);
            _syncReport.FastBlocksBodies.MarkEnd();
        }

        public override Task<SimpleBodiesSyncBatch> PrepareRequest()
        {
            SimpleBodiesSyncBatch batch = null;
            if (ShouldBuildANewBatch())
            {
                SimpleBodiesSyncBatch simple = new SimpleBodiesSyncBatch();
                simple.Prioritized = true;
                simple.Infos = _fastStatusList.GetInfosForBatch(_requestSize);
                if (simple.Infos[0] != null)
                {
                    batch = simple;
                }
            }

            return Task.FromResult(batch);
        }

        public override SyncResponseHandlingResult HandleResponse(SimpleBodiesSyncBatch batch)
        {
            batch.MarkHandlingStart();
            try
            {
                int added = InsertBodies(batch);
                return added == 0
                    ? SyncResponseHandlingResult.NoProgress
                    : SyncResponseHandlingResult.OK;
            }
            finally
            {
                batch.MarkHandlingEnd();
            }
        }

        private bool TryPrepareBlock(BlockInfo blockInfo, BlockBody blockBody, out Block block)
        {
            BlockHeader header = _blockTree.FindHeader(blockInfo.BlockHash);
            bool txRootIsValid = new TxTrie(blockBody.Transactions).RootHash != header.TxRoot;
            bool ommersHashIsValid = OmmersHash.Calculate(blockBody.Ommers) != header.OmmersHash;
            if (txRootIsValid && ommersHashIsValid)
            {
                block = null;
            }
            else
            {
                block = new Block(header, blockBody);
            }

            return block != null;
        }

        private int InsertBodies(SimpleBodiesSyncBatch batch)
        {
            bool hasBreachedProtocol = false;
            int validResponsesCount = 0;

            for (int i = 0; i < batch.Infos.Length; i++)
            {
                BlockInfo blockInfo = batch.Infos[i];
                BlockBody body = (batch.Response?.Length ?? 0) <= i
                    ? null
                    : batch.Response![i];

                if (body != null)
                {
                    Block block = null;
                    bool isValid = !hasBreachedProtocol && TryPrepareBlock(blockInfo, body, out block);
                    if (isValid)
                    {
                        validResponsesCount++;
                        _blockTree.Insert(block);
                        _fastStatusList.MarkInserted(block.Number);
                    }
                    else
                    {
                        hasBreachedProtocol = true;
                        if (_logger.IsWarn) _logger.Warn($"{batch} - reporting INVALID - tx or ommers");
                        _syncPeerPool.ReportBreachOfProtocol(batch.ResponseSourcePeer, "invalid tx or ommers root");
                        _fastStatusList.MarkUnknown(blockInfo.BlockNumber);
                    }
                }
                else
                {
                    _fastStatusList.MarkUnknown(blockInfo.BlockNumber);
                }
            }

            _syncReport.FastBlocksBodies.Update(_pivotNumber - _fastStatusList.LowestInsertWithoutGaps);
            _syncReport.BodiesInQueue.Update(_fastStatusList.QueueSize);

            _logger.Warn($"Response from {batch.ResponseSourcePeer} with {validResponsesCount}/{batch.Infos.Length}");

            lock (_fastStatusList)
            {
                if (validResponsesCount == batch.Infos.Length)
                {
                    _requestSize = Math.Min(1024, _requestSize * 2);
                }

                if (validResponsesCount == 0)
                {
                    _requestSize = Math.Max(4, _requestSize / 2);
                }
            }

            return validResponsesCount;
        }
    }
}