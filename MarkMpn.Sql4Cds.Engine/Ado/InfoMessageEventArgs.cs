﻿using System;
using System.Collections.Generic;
using System.Text;
using MarkMpn.Sql4Cds.Engine.ExecutionPlan;

namespace MarkMpn.Sql4Cds.Engine
{
    public class InfoMessageEventArgs : EventArgs
    {
        public InfoMessageEventArgs(IRootExecutionPlanNode node, string message)
        {
            Statement = node;
            Message = message;
        }

        public IRootExecutionPlanNode Statement { get; }

        public string Message { get; }
    }
}
