﻿using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Microsoft.Xrm.Sdk;

namespace MarkMpn.Sql4Cds.Engine.ExecutionPlan
{
    /// <summary>
    /// Produces aggregate values where the input data is already sorted by the grouping keys
    /// </summary>
    class StreamAggregateNode : BaseAggregateNode
    {
        public override IDataExecutionPlanNode FoldQuery(IDictionary<string, DataSource> dataSources, IQueryExecutionOptions options, IDictionary<string, Type> parameterTypes, IList<OptimizerHint> hints)
        {
            Source = Source.FoldQuery(dataSources, options, parameterTypes, hints);
            Source.Parent = this;
            return this;
        }

        public override INodeSchema GetSchema(IDictionary<string, DataSource> dataSources, IDictionary<string, Type> parameterTypes)
        {
            var schema = base.GetSchema(dataSources, parameterTypes);
            var groupByCols = GetGroupingColumns(schema);
            ((NodeSchema)schema).SortOrder.AddRange(groupByCols);
            
            return schema;
        }

        protected override IEnumerable<Entity> ExecuteInternal(IDictionary<string, DataSource> dataSources, IQueryExecutionOptions options, IDictionary<string, Type> parameterTypes, IDictionary<string, object> parameterValues)
        {
            var schema = Source.GetSchema(dataSources, parameterTypes);
            var groupByCols = GetGroupingColumns(schema);

            var isScalarAggregate = IsScalarAggregate;

            InitializeAggregates(schema, parameterTypes);
            CompoundKey currentGroup = null;
            var aggregates = CreateAggregateFunctions(parameterValues, options, false);
            var states = isScalarAggregate ? ResetAggregates(aggregates) : null;

            foreach (var entity in Source.Execute(dataSources, options, parameterTypes, parameterValues))
            {
                if (!isScalarAggregate || currentGroup != null)
                {
                    var key = new CompoundKey(entity, groupByCols);
                    var startNewGroup = currentGroup == null;

                    if (currentGroup != null && !key.Equals(currentGroup))
                    {
                        // We've reached the end of the previous group - return that row now
                        var result = new Entity();

                        for (var i = 0; i < GroupBy.Count; i++)
                            result[groupByCols[i]] = currentGroup.Values[i];

                        foreach (var aggregate in GetValues(states))
                            result[aggregate.Key] = aggregate.Value;

                        yield return result;

                        startNewGroup = true;
                    }

                    if (startNewGroup)
                    {
                        currentGroup = key;
                        states = ResetAggregates(aggregates);
                    }
                }

                foreach (var func in states.Values)
                    func.AggregateFunction.NextRecord(entity, func.State);
            }

            if (states != null)
            {
                // For scalar aggregates, or for non-scalar aggregates where we've found at least one group, we need to
                // return the values for the final group
                var result = new Entity();

                for (var i = 0; i < GroupBy.Count; i++)
                    result[groupByCols[i]] = currentGroup.Values[i];

                foreach (var aggregate in GetValues(states))
                    result[aggregate.Key] = aggregate.Value;

                yield return result;
            }
        }
    }
}