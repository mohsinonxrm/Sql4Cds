﻿using System;
using System.Reflection;
using System.Threading.Tasks;
using MarkMpn.Sql4Cds.Engine;
using Microsoft.Xrm.Sdk.Metadata;

namespace MarkMpn.Sql4Cds.SSMS
{
    internal class QueryExecutionOptions : IQueryExecutionOptions
    {
        private readonly SqlScriptEditorControlWrapper _sqlScriptEditorControl;

        public QueryExecutionOptions(SqlScriptEditorControlWrapper sqlScriptEditorControl)
        {
            _sqlScriptEditorControl = sqlScriptEditorControl;
        }

        public bool Cancelled { get; private set; }

        public bool BlockUpdateWithoutWhere => false;

        public bool BlockDeleteWithoutWhere => false;

        public bool UseBulkDelete => false;

        public int BatchSize => 1;

        public bool UseTDSEndpoint => true;

        public bool UseRetrieveTotalRecordCount => false;

        public int LocaleId => 1033;

        public int MaxDegreeOfParallelism => 1;

        public bool ConfirmDelete(int count, EntityMetadata meta)
        {
            if (count == 1)
                _sqlScriptEditorControl.Results.AddStringToMessages($"Deleting 1 {meta.DisplayName?.UserLocalizedLabel?.Label ?? meta.LogicalName}...\r\n");
            else
                _sqlScriptEditorControl.Results.AddStringToMessages($"Deleting {count:N0} {meta.DisplayCollectionName?.UserLocalizedLabel?.Label ?? meta.LogicalCollectionName ?? meta.LogicalName}...\r\n");

            return true;
        }

        public bool ConfirmUpdate(int count, EntityMetadata meta)
        {
            if (count == 1)
                _sqlScriptEditorControl.Results.AddStringToMessages($"Updating 1 {meta.DisplayName?.UserLocalizedLabel?.Label ?? meta.LogicalName}...\r\n");
            else
                _sqlScriptEditorControl.Results.AddStringToMessages($"Updating {count:N0} {meta.DisplayCollectionName?.UserLocalizedLabel?.Label ?? meta.LogicalCollectionName ?? meta.LogicalName}...\r\n");

            return true;
        }

        public bool ContinueRetrieve(int count)
        {
            return true;
        }

        public void Progress(double? progress, string message)
        {
            if (progress != null)
                _sqlScriptEditorControl.Results.OnQueryProgressUpdateEstimate(progress.Value);
        }

        public Task Task { get; set; }

        public void Cancel()
        {
            _sqlScriptEditorControl.Cancelling();
            Task.ContinueWith(t => _sqlScriptEditorControl.DoCancelExec());
            Cancelled = true;
        }
    }
}