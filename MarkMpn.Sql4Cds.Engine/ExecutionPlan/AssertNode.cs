﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Xrm.Sdk;

namespace MarkMpn.Sql4Cds.Engine.ExecutionPlan
{
    /// <summary>
    /// Checks that each row in the results meets an expected condition
    /// </summary>
    public class AssertNode : BaseNode
    {
        /// <summary>
        /// The data source for the assertion
        /// </summary>
        public IExecutionPlanNode Source { get; set; }

        /// <summary>
        /// The function that must be true for each entity in the <see cref="Source"/>
        /// </summary>
        public Func<Entity,bool> Assertion { get; set; }

        /// <summary>
        /// The error message that is generated if any record in the <see cref="Source"/> fails to meet the <see cref="Assertion"/>
        /// </summary>
        public string ErrorMessage { get; set; }

        public override IEnumerable<Entity> Execute(IOrganizationService org, IAttributeMetadataCache metadata, IQueryExecutionOptions options, IDictionary<string, Type> parameterTypes, IDictionary<string,object> parameterValues)
        {
            foreach (var entity in Source.Execute(org, metadata, options, parameterTypes, parameterValues))
            {
                if (!Assertion(entity))
                    throw new ApplicationException(ErrorMessage);

                yield return entity;
            }
        }

        public override NodeSchema GetSchema(IAttributeMetadataCache metadata, IDictionary<string, Type> parameterTypes)
        {
            return Source.GetSchema(metadata, parameterTypes);
        }

        public override IEnumerable<IExecutionPlanNode> GetSources()
        {
            yield return Source;
        }

        public override IExecutionPlanNode MergeNodeDown(IAttributeMetadataCache metadata, IQueryExecutionOptions options, IDictionary<string, Type> parameterTypes)
        {
            Source = Source.MergeNodeDown(metadata, options, parameterTypes);
            return this;
        }

        public override void AddRequiredColumns(IAttributeMetadataCache metadata, IDictionary<string, Type> parameterTypes, IList<string> requiredColumns)
        {
            Source.AddRequiredColumns(metadata, parameterTypes, requiredColumns);
        }
    }
}