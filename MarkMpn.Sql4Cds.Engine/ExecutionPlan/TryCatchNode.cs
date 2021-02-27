﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Xrm.Sdk;

namespace MarkMpn.Sql4Cds.Engine.ExecutionPlan
{
    public class TryCatchNode : BaseNode
    {
        public IExecutionPlanNode TrySource { get; set; }
        public IExecutionPlanNode CatchSource { get; set; }
        public Func<Exception,bool> ExceptionFilter { get; set; }

        public override IEnumerable<Entity> Execute(IOrganizationService org, IAttributeMetadataCache metadata, IQueryExecutionOptions options, IDictionary<string, Type> parameterTypes, IDictionary<string, object> parameterValues)
        {
            try
            {
                return TrySource.Execute(org, metadata, options, parameterTypes, parameterValues);
            }
            catch (Exception ex)
            {
                if (ExceptionFilter != null && !ExceptionFilter(ex))
                    throw;

                return CatchSource.Execute(org, metadata, options, parameterTypes, parameterValues);
            }
        }

        public override NodeSchema GetSchema(IAttributeMetadataCache metadata, IDictionary<string, Type> parameterTypes)
        {
            return TrySource.GetSchema(metadata, parameterTypes);
        }

        public override IEnumerable<IExecutionPlanNode> GetSources()
        {
            yield return TrySource;
            yield return CatchSource;
        }

        public override IExecutionPlanNode FoldQuery(IAttributeMetadataCache metadata, IQueryExecutionOptions options, IDictionary<string, Type> parameterTypes)
        {
            TrySource = TrySource.FoldQuery(metadata, options, parameterTypes);
            CatchSource = CatchSource.FoldQuery(metadata, options, parameterTypes);
            return this;
        }

        public override void AddRequiredColumns(IAttributeMetadataCache metadata, IDictionary<string, Type> parameterTypes, IList<string> requiredColumns)
        {
            TrySource.AddRequiredColumns(metadata, parameterTypes, requiredColumns);
            CatchSource.AddRequiredColumns(metadata, parameterTypes, requiredColumns);
        }
    }
}
