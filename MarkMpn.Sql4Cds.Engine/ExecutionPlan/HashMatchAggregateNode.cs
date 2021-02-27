﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.ServiceModel;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Serialization;
using MarkMpn.Sql4Cds.Engine.FetchXml;
using MarkMpn.Sql4Cds.Engine.QueryExtensions;
using MarkMpn.Sql4Cds.Engine.Visitors;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Microsoft.Xrm.Sdk;

namespace MarkMpn.Sql4Cds.Engine.ExecutionPlan
{
    public class HashMatchAggregateNode : BaseNode
    {
        class GroupingKey
        {
            private readonly int _hashCode;

            public GroupingKey(Entity entity, List<ColumnReferenceExpression> columns)
            {
                Values = columns.Select(col => entity[col.GetColumnName()]).ToList();
                _hashCode = 0;

                foreach (var value in Values)
                {
                    if (value == null)
                        continue;

                    _hashCode ^= StringComparer.OrdinalIgnoreCase.GetHashCode(value);
                }
            }

            public List<object> Values { get; }

            public override int GetHashCode() => _hashCode;

            public override bool Equals(object obj)
            {
                var other = (GroupingKey)obj;

                for (var i = 0; i < Values.Count; i++)
                {
                    if (Values[i] == null && other.Values[i] == null)
                        continue;

                    if (Values[i] == null || other.Values[i] == null)
                        return false;

                    if (!StringComparer.OrdinalIgnoreCase.Equals(Values[i], other.Values[i]))
                        return false;
                }

                return true;
            }
        }

        public List<ColumnReferenceExpression> GroupBy { get; } = new List<ColumnReferenceExpression>();

        public Dictionary<string, Aggregate> Aggregates { get; } = new Dictionary<string, Aggregate>();

        public IExecutionPlanNode Source { get; set; }

        public override IEnumerable<Entity> Execute(IOrganizationService org, IAttributeMetadataCache metadata, IQueryExecutionOptions options, IDictionary<string, Type> parameterTypes, IDictionary<string, object> parameterValues)
        {
            var groups = new Dictionary<GroupingKey, Dictionary<string,AggregateFunction>>();
            var schema = Source.GetSchema(metadata, parameterTypes);

            foreach (var entity in Source.Execute(org, metadata, options, parameterTypes, parameterValues))
            {
                var key = new GroupingKey(entity, GroupBy);

                if (!groups.TryGetValue(key, out var values))
                {
                    values = new Dictionary<string,AggregateFunction>();

                    foreach (var aggregate in Aggregates)
                    {
                        switch (aggregate.Value.AggregateType)
                        {
                            case AggregateType.Average:
                                values[aggregate.Key] = new Average(e => aggregate.Value.Expression.GetValue(e, schema, parameterTypes, parameterValues));
                                break;

                            case AggregateType.Count:
                                values[aggregate.Key] = new CountColumn(e => aggregate.Value.Expression.GetValue(e, schema, parameterTypes, parameterValues));
                                break;

                            case AggregateType.CountStar:
                                values[aggregate.Key] = new Count(null);
                                break;

                            case AggregateType.Max:
                                values[aggregate.Key] = new Max(e => aggregate.Value.Expression.GetValue(e, schema, parameterTypes, parameterValues));
                                break;

                            case AggregateType.Min:
                                values[aggregate.Key] = new Min(e => aggregate.Value.Expression.GetValue(e, schema, parameterTypes, parameterValues));
                                break;

                            case AggregateType.Sum:
                                values[aggregate.Key] = new Sum(e => aggregate.Value.Expression.GetValue(e, schema, parameterTypes, parameterValues));
                                break;

                            case AggregateType.First:
                                values[aggregate.Key] = new First(e => aggregate.Value.Expression.GetValue(e, schema, parameterTypes, parameterValues));
                                break;

                            default:
                                throw new QueryExecutionException(null, "Unknown aggregate type");
                        }
                    }
                    
                    groups[key] = values;
                }

                foreach (var func in values.Values)
                    func.NextRecord(entity);
            }

            foreach (var group in groups)
            {
                var result = new Entity();

                for (var i = 0; i < GroupBy.Count; i++)
                    result[GroupBy[i].GetColumnName()] = group.Key.Values[i];

                foreach (var aggregate in group.Value)
                    result[aggregate.Key] = aggregate.Value.Value;

                yield return result;
            }
        }

        public override NodeSchema GetSchema(IAttributeMetadataCache metadata, IDictionary<string, Type> parameterTypes)
        {
            var sourceSchema = Source.GetSchema(metadata, parameterTypes);
            var schema = new NodeSchema();

            foreach (var group in GroupBy)
            {
                var colName = group.GetColumnName();
                sourceSchema.ContainsColumn(colName, out var normalized);
                schema.Schema[normalized] = sourceSchema.Schema[normalized];

                foreach (var alias in sourceSchema.Aliases.Where(a => a.Value.Contains(normalized)))
                {
                    if (!schema.Aliases.TryGetValue(alias.Key, out var aliases))
                    {
                        aliases = new List<string>();
                        schema.Aliases[alias.Key] = aliases;
                    }

                    aliases.Add(normalized);
                }
            }

            foreach (var aggregate in Aggregates)
            {
                Type aggregateType;

                switch (aggregate.Value.AggregateType)
                {
                    case AggregateType.Count:
                    case AggregateType.CountStar:
                        aggregateType = typeof(int);
                        break;

                    default:
                        aggregateType = aggregate.Value.Expression.GetType(sourceSchema, parameterTypes);
                        break;
                }

                schema.Schema[aggregate.Key] = aggregateType;
            }

            return schema;
        }

        public override IEnumerable<IExecutionPlanNode> GetSources()
        {
            yield return Source;
        }

        public override IExecutionPlanNode MergeNodeDown(IAttributeMetadataCache metadata, IQueryExecutionOptions options, IDictionary<string, Type> parameterTypes)
        {
            Source = Source.MergeNodeDown(metadata, options, parameterTypes);

            // Special case for using RetrieveTotalRecordCount instead of FetchXML
            if (options.UseRetrieveTotalRecordCount &&
                Source is FetchXmlScan fetch &&
                (fetch.Entity.Items == null || fetch.Entity.Items.Length == 0) &&
                GroupBy.Count == 0 &&
                Aggregates.Count == 1 &&
                Aggregates.Single().Value.AggregateType == AggregateType.CountStar)
            {
                var count = new RetrieveTotalRecordCountNode { EntityName = fetch.Entity.name };
                var countName = count.GetSchema(metadata, parameterTypes).Schema.Single().Key;

                if (countName == Aggregates.Single().Key)
                    return count;

                var rename = new ComputeScalarNode
                {
                    Source = count,
                    Columns =
                {
                    [Aggregates.Single().Key] = new ColumnReferenceExpression
                    {
                        MultiPartIdentifier = new MultiPartIdentifier
                        {
                            Identifiers = { new Identifier { Value = countName } }
                        }
                    }
                }
                };

                return rename;
            }

            if (Source is FetchXmlScan || Source is ComputeScalarNode computeScalar && computeScalar.Source is FetchXmlScan)
            {
                // Check if all the aggregates & groupings can be done in FetchXML. Can only convert them if they can ALL
                // be handled - if any one needs to be calculated manually, we need to calculate them all
                foreach (var agg in Aggregates)
                {
                    if (agg.Value.Expression != null && !(agg.Value.Expression is ColumnReferenceExpression))
                        return this;

                    if (agg.Value.Distinct && agg.Value.AggregateType != ExecutionPlan.AggregateType.Count)
                        return this;

                    if (agg.Value.AggregateType == AggregateType.First)
                        return this;
                }

                var fetchXml = Source as FetchXmlScan;
                computeScalar = Source as ComputeScalarNode;

                var partnames = new Dictionary<string, FetchXml.DateGroupingType>(StringComparer.OrdinalIgnoreCase)
                {
                    ["year"] = DateGroupingType.year,
                    ["yy"] = DateGroupingType.year,
                    ["yyyy"] = DateGroupingType.year,
                    ["quarter"] = DateGroupingType.quarter,
                    ["qq"] = DateGroupingType.quarter,
                    ["q"] = DateGroupingType.quarter,
                    ["month"] = DateGroupingType.month,
                    ["mm"] = DateGroupingType.month,
                    ["m"] = DateGroupingType.month,
                    ["day"] = DateGroupingType.day,
                    ["dd"] = DateGroupingType.day,
                    ["d"] = DateGroupingType.day,
                    ["week"] = DateGroupingType.week,
                    ["wk"] = DateGroupingType.week,
                    ["ww"] = DateGroupingType.week
                };

                if (computeScalar != null)
                {
                    fetchXml = (FetchXmlScan)computeScalar.Source;

                    // Groupings may be on DATEPART function, which will have been split into separate Compute Scalar node. Check if all the scalar values
                    // being computed are DATEPART functions that can be converted to FetchXML and are used as groupings
                    foreach (var scalar in computeScalar.Columns)
                    {
                        if (!(scalar.Value is FunctionCall func) ||
                            !func.FunctionName.Value.Equals("DATEPART", StringComparison.OrdinalIgnoreCase) ||
                            func.Parameters.Count != 2 ||
                            !(func.Parameters[0] is ColumnReferenceExpression datePartType) ||
                            !(func.Parameters[1] is ColumnReferenceExpression datePartCol))
                            return this;

                        if (!GroupBy.Any(g => g.MultiPartIdentifier.Identifiers.Count == 1 && g.MultiPartIdentifier.Identifiers[0].Value == scalar.Key))
                            return this;

                        if (!partnames.ContainsKey(datePartType.GetColumnName()))
                            return this;
                    }
                }

                // Check none of the grouped columns are virtual attributes - FetchXML doesn't support grouping by them
                var fetchSchema = fetchXml.GetSchema(metadata, parameterTypes);
                foreach (var group in GroupBy)
                {
                    if (!fetchSchema.ContainsColumn(group.GetColumnName(), out var groupCol))
                        continue;

                    var parts = groupCol.Split('.');
                    string entityName;

                    if (parts[0] == fetchXml.Alias)
                        entityName = fetchXml.Entity.name;
                    else
                        entityName = fetchXml.Entity.FindLinkEntity(parts[0]).name;

                    var attr = metadata[entityName].Attributes.Single(a => a.LogicalName == parts[1]);

                    if (attr.AttributeOf != null)
                        return this;
                }

                // FetchXML aggregates can trigger an AggregateQueryRecordLimitExceeded error. Clone the non-aggregate FetchXML
                // so we can try to run the native aggregate version but fall back to in-memory processing where necessary
                var serializer = new XmlSerializer(typeof(FetchXml.FetchType));

                var clonedFetchXml = new FetchXmlScan
                {
                    Alias = fetchXml.Alias,
                    AllPages = fetchXml.AllPages,
                    FetchXml = (FetchXml.FetchType)serializer.Deserialize(new StringReader(fetchXml.FetchXmlString)),
                    ReturnFullSchema = fetchXml.ReturnFullSchema
                };

                if (Source == fetchXml)
                    Source = clonedFetchXml;
                else
                    computeScalar.Source = clonedFetchXml;

                fetchXml.FetchXml.aggregate = true;
                fetchXml.FetchXml.aggregateSpecified = true;
                fetchXml.FetchXml = fetchXml.FetchXml;

                var schema = Source.GetSchema(metadata, parameterTypes);

                foreach (var grouping in GroupBy)
                {
                    var colName = grouping.GetColumnName();
                    var alias = colName;
                    DateGroupingType? dateGrouping = null;

                    if (computeScalar != null && computeScalar.Columns.TryGetValue(colName, out var datePart))
                    {
                        dateGrouping = partnames[((ColumnReferenceExpression)((FunctionCall)datePart).Parameters[0]).GetColumnName()];
                        colName = ((ColumnReferenceExpression)((FunctionCall)datePart).Parameters[1]).GetColumnName();
                    }

                    schema.ContainsColumn(colName, out colName);

                    var attribute = AddAttribute(fetchXml, colName, a => a.groupby == FetchBoolType.@true && a.alias == alias, metadata, out _);
                    attribute.groupby = FetchBoolType.@true;
                    attribute.groupbySpecified = true;
                    attribute.alias = alias;

                    if (dateGrouping != null)
                    {
                        attribute.dategrouping = dateGrouping.Value;
                        attribute.dategroupingSpecified = true;
                    }
                }

                foreach (var agg in Aggregates)
                {
                    var col = (ColumnReferenceExpression)agg.Value.Expression;
                    var colName = col == null ? schema.PrimaryKey : col.GetColumnName();
                    schema.ContainsColumn(colName, out colName);

                    FetchXml.AggregateType aggregateType;

                    switch (agg.Value.AggregateType)
                    {
                        case ExecutionPlan.AggregateType.Average:
                            aggregateType = FetchXml.AggregateType.avg;
                            break;

                        case ExecutionPlan.AggregateType.Count:
                            aggregateType = FetchXml.AggregateType.countcolumn;
                            break;

                        case ExecutionPlan.AggregateType.CountStar:
                            aggregateType = FetchXml.AggregateType.count;
                            break;

                        case ExecutionPlan.AggregateType.Max:
                            aggregateType = FetchXml.AggregateType.max;
                            break;

                        case ExecutionPlan.AggregateType.Min:
                            aggregateType = FetchXml.AggregateType.min;
                            break;

                        case ExecutionPlan.AggregateType.Sum:
                            aggregateType = FetchXml.AggregateType.sum;
                            break;

                        default:
                            throw new ArgumentOutOfRangeException();
                    }

                    var attribute = AddAttribute(fetchXml, colName, a => a.aggregate == aggregateType && a.alias == agg.Key, metadata, out _);
                    attribute.aggregate = aggregateType;
                    attribute.aggregateSpecified = true;
                    attribute.alias = agg.Key;
                }

                return new TryCatchNode
                {
                    TrySource = fetchXml,
                    CatchSource = this,
                    ExceptionFilter = IsAggregateQueryRecordLimitExceededException
                };
            }

            return this;
        }

        public override void AddRequiredColumns(IAttributeMetadataCache metadata, IDictionary<string, Type> parameterTypes, IList<string> requiredColumns)
        {
            // Columns required by previous nodes must be derived from this node, so no need to pass them through.
            // Just calculate the columns that are required to calculate the groups & aggregates
            var scalarRequiredColumns = new List<string>();
            if (GroupBy != null)
                scalarRequiredColumns.AddRange(GroupBy.Select(g => g.GetColumnName()));

            scalarRequiredColumns.AddRange(Aggregates.Where(agg => agg.Value.Expression != null).SelectMany(agg => agg.Value.Expression.GetColumns()).Distinct());

            Source.AddRequiredColumns(metadata, parameterTypes, scalarRequiredColumns);
        }

        private bool IsAggregateQueryRecordLimitExceededException(Exception ex)
        {
            if (!(ex is FaultException<OrganizationServiceFault> fault))
                return false;

            /*
             * 0x8004E023 / -2147164125	
             * Name: AggregateQueryRecordLimitExceeded
             * Message: The maximum record limit is exceeded. Reduce the number of records.
             */
            return fault.Detail.ErrorCode == -2147164125;
        }
    }
}