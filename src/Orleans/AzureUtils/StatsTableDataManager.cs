/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

﻿using System;
using System.Collections.Generic;
using System.Data.Services.Common;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.StorageClient;
using Orleans.Runtime;

namespace Orleans.AzureUtils
{
    [Serializable]
    [DataServiceKey("PartitionKey", "RowKey")]
    internal class StatsTableData : TableServiceEntity
    {
        public string DeploymentId { get; set; }
        public DateTime Time { get; set; }
        public string Address { get; set; }
        public string Name { get; set; }
        public string HostName { get; set; }

        public string Statistic { get; set; }
        public string StatValue { get; set; }
        public bool IsDelta { get; set; }

        public override string ToString()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("StatsTableData[");
            sb.Append(" PartitionKey=").Append(PartitionKey);
            sb.Append(" RowKey=").Append(RowKey);

            sb.Append(" DeploymentId=").Append(DeploymentId);
            sb.Append(" Time=").Append(Time);
            sb.Append(" Address=").Append(Address);
            sb.Append(" Name=").Append(Name);
            sb.Append(" HostName=").Append(HostName);
            sb.Append(" Statistic=").Append(Statistic);
            sb.Append(" StatValue=").Append(StatValue);
            sb.Append(" IsDelta=").Append(IsDelta);
            sb.Append(" ]");
            return sb.ToString();
        }
    }

    internal class StatsTableDataManager : IStatisticsPublisher
    {
        private readonly string deploymentId;
        private readonly string address;
        private readonly string name;
        private readonly bool isSilo;
        private readonly long clientEpoch;
        private int counter;
        private readonly string myHostName;
        private const string DATE_FORMAT = "yyyy-MM-dd";

        private readonly AzureTableDataManager<StatsTableData> tableManager;
        private readonly TraceLogger logger;

        private static readonly TimeSpan initTimeout = AzureTableDefaultPolicies.TableCreationTimeout;

        private StatsTableDataManager(bool isSilo, string storageConnectionString, string deploymentId, string address, string siloName, string hostName)
        {
            logger = TraceLogger.GetLogger(this.GetType().Name, TraceLogger.LoggerType.Runtime);
            this.deploymentId = deploymentId;
            this.address = address;
            name = siloName;
            myHostName = hostName;
            this.isSilo = isSilo;
            if (!this.isSilo)
            {
                clientEpoch = SiloAddress.AllocateNewGeneration();
            }
            counter = 0;
            var tableName = isSilo ? "OrleansSiloStatistics" : "OrleansClientStatistics";
            tableManager = new AzureTableDataManager<StatsTableData>(tableName, storageConnectionString, logger);
        }

        internal static async Task<IStatisticsPublisher> GetManager(bool isSilo, string storageConnectionString, string deploymentId, string address, string siloName, string hostName)
        {
            var instance = new StatsTableDataManager(isSilo, storageConnectionString, deploymentId, address, siloName, hostName);
            await instance.tableManager.InitTableAsync().WithTimeout(initTimeout);
            return instance;
        }

        public Task ReportStats(List<ICounter> statsCounters)
        {
            var bulkPromises = new List<Task>();
            var data = new List<StatsTableData>();
            foreach (ICounter count in statsCounters.Where(cs => cs.Storage == CounterStorage.LogAndTable).OrderBy(cs => cs.Name))
            {
                if (data.Count >= AzureTableDefaultPolicies.MAX_BULK_UPDATE_ROWS)
                {
                    // Write intermediate batch
                    bulkPromises.Add(tableManager.BulkInsertTableEntries(data));
                    data.Clear();
                }

                StatsTableData statsTableEntry = PopulateStatsTableDataEntry(count);
                if (statsTableEntry == null) continue; // Skip blank entries
                if (logger.IsVerbose2) logger.Verbose2("Preparing to bulk insert {1} stats table entry: {0}", statsTableEntry, isSilo ? "silo" : "");
                data.Add(statsTableEntry);
            }
            if (data.Count > 0)
            {
                // Write final batch
                bulkPromises.Add(tableManager.BulkInsertTableEntries(data));
            }
            return Task.WhenAll(bulkPromises);
        }

        private StatsTableData PopulateStatsTableDataEntry(ICounter statsCounter)
        {
            string statValue = statsCounter.IsValueDelta ? statsCounter.GetDeltaString() : statsCounter.GetValueString();
            var entry = new StatsTableData { StatValue = statValue };

            if ("0".Equals(entry.StatValue))
            {
                // Skip writing empty records
                return null;
            }

            counter++;
            var time = DateTime.UtcNow;
            var timeStr = time.ToString(DATE_FORMAT, CultureInfo.InvariantCulture);
            entry.PartitionKey = deploymentId + ":" + timeStr;
            var counterStr = String.Format("{0:000000}", counter);
            if (isSilo)
            {
                entry.RowKey = name + ":" + counterStr;
            }
            else
            {
                entry.RowKey = name + ":" + clientEpoch + ":" + counterStr;
            }
            entry.DeploymentId = deploymentId;
            entry.Time = time;
            entry.Address = address;
            entry.Name = name;
            entry.HostName = myHostName;
            entry.Statistic = statsCounter.Name;
            entry.IsDelta = statsCounter.IsValueDelta;
            return entry;
        }
    }
}
