﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Microsoft.Xrm.Sdk;

namespace MarkMpn.Sql4Cds.Engine.ExecutionPlan
{
    /// <summary>
    /// Checks that each row in the results meets an expected condition
    /// </summary>
    class AssertNode : BaseDataNode, ISingleSourceExecutionPlanNode
    {
        /// <summary>
        /// The data source for the assertion
        /// </summary>
        [Browsable(false)]
        public IDataExecutionPlanNodeInternal Source { get; set; }

        /// <summary>
        /// The function that must be true for each entity in the <see cref="Source"/>
        /// </summary>
        [Browsable(false)]
        public Func<Entity,bool> Assertion { get; set; }

        /// <summary>
        /// The error message that is generated if any record in the <see cref="Source"/> fails to meet the <see cref="Assertion"/>
        /// </summary>
        [Category("Assert")]
        [Description("The error message that is generated if any record in the source fails to meet the assertion")]
        [DisplayName("Error Message")]
        public string ErrorMessage { get; set; }

        protected override IEnumerable<Entity> ExecuteInternal(NodeExecutionContext context)
        {
            foreach (var entity in Source.Execute(context))
            {
                if (!Assertion(entity))
                    throw new ApplicationException(ErrorMessage);

                yield return entity;
            }
        }

        public override INodeSchema GetSchema(NodeCompilationContext context)
        {
            return Source.GetSchema(context);
        }

        public override IEnumerable<IExecutionPlanNode> GetSources()
        {
            yield return Source;
        }

        public override IDataExecutionPlanNodeInternal FoldQuery(NodeCompilationContext context, IList<OptimizerHint> hints)
        {
            Source = Source.FoldQuery(context, hints);
            Source.Parent = this;
            return this;
        }

        public override void AddRequiredColumns(NodeCompilationContext context, IList<string> requiredColumns)
        {
            Source.AddRequiredColumns(context, requiredColumns);
        }

        protected override RowCountEstimate EstimateRowsOutInternal(NodeCompilationContext context)
        {
            return Source.EstimateRowsOut(context);
        }

        public override object Clone()
        {
            var clone = new AssertNode
            {
                Source = (IDataExecutionPlanNodeInternal)Source.Clone(),
                Assertion = Assertion,
                ErrorMessage = ErrorMessage
            };

            clone.Source.Parent = clone;
            return clone;
        }
    }
}
