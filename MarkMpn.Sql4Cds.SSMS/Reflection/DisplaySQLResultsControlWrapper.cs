﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.SqlServer.Management.QueryExecution;

namespace MarkMpn.Sql4Cds.SSMS
{
    class DisplaySQLResultsControlWrapper : ReflectionObjectBase
    {
        private const string Banner =
        @"
  ___  ___  _      _ _     ___ ___  ___ 
 / __|/ _ \| |    | | |   / __|   \/ __|
 \__ \ (_) | |__  |_  _| | (__| |) \__ \
 |___/\__\_\____|   |_|   \___|___/|___/  v{version}

 INSERT/UPDATE/DELETE commands are implemented by SQL 4 CDS
 and not supported by Microsoft
 https://markcarrington.dev/sql-4-cds/

";

        private static readonly Type QESQLExecutionOptions;
        private static readonly Type QESQLBatch;
        private static readonly Type ScriptExecutionResult;
        private static readonly Type ScriptExecutionCompletedEventArgs;

        static DisplaySQLResultsControlWrapper()
        {
            QESQLExecutionOptions = GetType("Microsoft.SqlServer.Management.QueryExecution.QESQLExecutionOptions, SQLEditors");
            QESQLBatch = GetType("Microsoft.SqlServer.Management.QueryExecution.QESQLBatch, SQLEditors");
            ScriptExecutionResult = GetType("Microsoft.SqlServer.Management.QueryExecution.ScriptExecutionResult, SQLEditors");
            ScriptExecutionCompletedEventArgs = GetType("Microsoft.SqlServer.Management.QueryExecution.ScriptExecutionCompletedEventArgs, SQLEditors");
        }

        public DisplaySQLResultsControlWrapper(object obj) : base(obj)
        {
            ResultsTabCtrl = (TabControl) GetField(obj, "m_resultsTabCtrl");
        }

        public TabControl ResultsTabCtrl { get; }

        public void StartExecution()
        {
            PrepareForExecution(false);
            IsExecuting = true;

            // Cancel method expects execution options to have been set at the start - create some default ones now
            var m_sqlExec = GetField(Target, "m_sqlExec");
            var execOptions = Activator.CreateInstance(QESQLExecutionOptions);
            SetField(m_sqlExec, "m_execOptions", execOptions);
            var batch = Activator.CreateInstance(QESQLBatch);
            SetField(m_sqlExec, "m_curBatch", batch);
            SetField(m_sqlExec, "m_batchConsumer", GetField(Target, "m_batchConsumer"));

            var currentVersion = Assembly.GetExecutingAssembly().GetName().Version;
            AddStringToMessages(Banner.Replace("{version}", currentVersion.ToString(3)));

            if (VersionChecker.Result.IsCompleted && !VersionChecker.Result.IsFaulted && VersionChecker.Result.Result > currentVersion)
            {
                AddStringToErrors(" An updated version of SQL 4 CDS is available", true);
                AddStringToMessages($" Update to v{VersionChecker.Result.Result.ToString(3)} available from https://markcarrington.dev/sql-4-cds/");
                AddStringToMessages("");
            }
        }

        private void PrepareForExecution(bool parse)
        {
            InvokeMethod(Target, "PrepareForExecution", parse);
        }

        private bool IsExecuting
        {
            get => (bool) GetField(Target, "m_bIsExecuting");
            set => SetField(Target, "m_bIsExecuting", value);
        }

        public void AddStringToMessages(string message)
        {
            InvokeMethod(Target, "AddStringToMessages", message, true);
        }

        public void AddStringToErrors(string message, int line, ITextSpan textSpan, bool flush)
        {
            InvokeMethod(Target, "AddStringToErrors", message, line, textSpan, flush);
        }

        public void AddStringToErrors(string message, bool flush)
        {
            InvokeMethod(Target, "AddStringToErrors", message, flush);
        }

        public void OnQueryProgressUpdateEstimate(double progress)
        {
            InvokeMethod(Target, "OnQueryProgressUpdateEstimate", progress);
        }

        public void OnSqlExecutionCompletedInt(int resultFlag)
        {
            var result = Enum.ToObject(ScriptExecutionResult, resultFlag);
            var resultsArg = Activator.CreateInstance(ScriptExecutionCompletedEventArgs, BindingFlags.CreateInstance | BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { result }, null);
            InvokeMethod(Target, "OnSqlExecutionCompletedInt", Target, resultsArg);
        }

        public void AddGridContainer(ResultSetAndGridContainerWrapper grid)
        {
            InvokeMethod(Target, "AddGridContainer", grid.Target);
        }
    }
}
