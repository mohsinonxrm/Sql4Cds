﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data.SqlTypes;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.ServiceModel;
using System.Text;
using System.Threading.Tasks;
using MarkMpn.Sql4Cds.Engine.FetchXml;
using MarkMpn.Sql4Cds.Engine.Visitors;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Metadata.Query;
using Microsoft.Xrm.Sdk.Query;

namespace MarkMpn.Sql4Cds.Engine.ExecutionPlan
{
    /// <summary>
    /// A base class for execution plan nodes that generate a stream of data
    /// </summary>
    abstract class BaseDataNode : BaseNode, IDataExecutionPlanNodeInternal
    {
        private int _executionCount;
        private readonly Timer _timer = new Timer();
        private TimeSpan _additionalDuration;
        private int _rowsOut;

        [Category("Statistics")]
        [Description("Returns the number of rows that the query optimizer estimates this node will generate")]
        [BrowsableInEstimatedPlan(true)]
        public int EstimatedRowsOut { get; protected set; }

        /// <summary>
        /// Returns the number of times the node has been executed
        /// </summary>
        public override int ExecutionCount => _executionCount;

        /// <summary>
        /// Returns the time that the node has taken to execute
        /// </summary>
        public override TimeSpan Duration => _timer.Duration + _additionalDuration;

        /// <summary>
        /// Returns the number of rows that the node has generated
        /// </summary>
        [Category("Statistics")]
        [Description("Returns the number of rows that the node has generated")]
        [BrowsableInEstimatedPlan(false)]
        public int RowsOut => _rowsOut;

        /// <summary>
        /// Executes the query and produces a stram of data in the results
        /// </summary>
        /// <param name="context">The context in which the node is being executed</param>
        /// <returns>A stream of <see cref="Entity"/> records</returns>
        public IEnumerable<Entity> Execute(NodeExecutionContext context)
        {
            if (context.Options.CancellationToken.IsCancellationRequested)
                yield break;

            // Track execution times roughly using Environment.TickCount. Stopwatch provides more accurate results
            // but gives a large performance penalty.
            IEnumerator<Entity> enumerator;

            using (_timer.Run())
            {
                try
                {
                    _executionCount++;

                    enumerator = ExecuteInternal(context).GetEnumerator();
                }
                catch (QueryExecutionException ex)
                {
                    if (ex.Node == null)
                        ex.Node = this;

                    throw;
                }
                catch (Exception ex)
                {
                    throw new QueryExecutionException(ex.Message, ex) { Node = this };
                }
            }

            while (!context.Options.CancellationToken.IsCancellationRequested)
            {
                Entity current;

                using (_timer.Run())
                { 
                    try
                    {
                        if (!enumerator.MoveNext())
                            break;

                        current = enumerator.Current;
                    }
                    catch (QueryExecutionException ex)
                    {
                        if (ex.Node == null)
                            ex.Node = this;

                        throw;
                    }
                    catch (Exception ex)
                    {
                        throw new QueryExecutionException(ex.Message, ex) { Node = this };
                    }
                }

                _rowsOut++;
                yield return current;
            }
        }

        /// <summary>
        /// Estimates the number of rows that will be returned by the node
        /// </summary>
        /// <param name="metadata">The <see cref="IAttributeMetadataCache"/> to use to get metadata</param>
        /// <param name="parameterTypes">A mapping of parameter names to their related types</param>
        /// <param name="tableSize">A cache of the number of records in each table</param>
        /// <returns>The number of rows the node is estimated to return</returns>
        public RowCountEstimate EstimateRowsOut(NodeCompilationContext context)
        {
            var estimate = EstimateRowsOutInternal(context);
            EstimatedRowsOut = estimate.Value;
            return estimate;
        }

        protected abstract RowCountEstimate EstimateRowsOutInternal(NodeCompilationContext context);

        protected void ParseEstimate(RowCountEstimate estimate, out int min, out int max, out bool isRange)
        {
            if (estimate is RowCountEstimateDefiniteRange range)
            {
                isRange = true;
                min = range.Minimum;
                max = range.Maximum;
            }
            else
            {
                isRange = false;
                min = estimate.Value;
                max = estimate.Value;
            }
        }

        /// <summary>
        /// Adds the execution statistics from another node into the summary for this node
        /// </summary>
        /// <param name="other">The other node to add the statistics from</param>
        public void MergeStatsFrom(BaseDataNode other)
        {
            _rowsOut += other.RowsOut;
            _additionalDuration += other.Duration;
            _executionCount += other.ExecutionCount;
        }

        /// <summary>
        /// Produces the data for the node without keeping track of any execution time statistics
        /// </summary>
        /// <param name="context">The context in which the node is being executed</param>
        /// <returns>A stream of <see cref="Entity"/> records</returns>
        protected abstract IEnumerable<Entity> ExecuteInternal(NodeExecutionContext context);

        /// <summary>
        /// Gets the details of columns produced by the node
        /// </summary>
        /// <param name="context">The context in which the node is being built</param>
        /// <returns>Details of the columns produced by the node</returns>
        public abstract INodeSchema GetSchema(NodeCompilationContext context);

        /// <summary>
        /// Attempts to fold this node into its source to simplify the query
        /// </summary>
        /// <param name="context">The context in which the node is being built</param>
        /// <param name="hints">Any optimizer hints to apply</param>
        /// <returns>The node that should be used in place of this node</returns>
        public abstract IDataExecutionPlanNodeInternal FoldQuery(NodeCompilationContext context, IList<OptimizerHint> hints);

        /// <summary>
        /// Translates filter criteria from ScriptDom to FetchXML
        /// </summary>
        /// <param name="context">The context the query is being built in</param>
        /// <param name="metadata">The <see cref="IAttributeMetadataCache"/> to use to get metadata</param>
        /// <param name="criteria">The SQL criteria to attempt to translate to FetchXML</param>
        /// <param name="schema">The schema of the node that the criteria apply to</param>
        /// <param name="allowedPrefix">The prefix of the table that the <paramref name="criteria"/> can be translated for, or <c>null</c> if any tables can be referenced</param>
        /// <param name="targetEntityName">The logical name of the root entity that the FetchXML query is targetting</param>
        /// <param name="targetEntityAlias">The alias of the root entity that the FetchXML query is targetting</param>
        /// <param name="items">The child items of the root entity in the FetchXML query</param>
        /// <param name="filter">The FetchXML version of the <paramref name="criteria"/> that is generated by this method</param>
        /// <returns><c>true</c> if the <paramref name="criteria"/> can be translated to FetchXML, or <c>false</c> otherwise</returns>
        protected bool TranslateFetchXMLCriteria(NodeCompilationContext context, IAttributeMetadataCache metadata, BooleanExpression criteria, INodeSchema schema, string allowedPrefix, string targetEntityName, string targetEntityAlias, object[] items, out filter filter)
        {
            if (!TranslateFetchXMLCriteria(context, metadata, criteria, schema, allowedPrefix, targetEntityName, targetEntityAlias, items, out var condition, out filter))
                return false;

            if (condition != null)
                filter = new filter { Items = new object[] { condition } };

            return true;
        }

        /// <summary>
        /// Translates filter criteria from ScriptDom to FetchXML
        /// </summary>
        /// <param name="context">The context the query is being built in</param>
        /// <param name="metadata">The <see cref="IAttributeMetadataCache"/> to use to get metadata</param>
        /// <param name="criteria">The SQL criteria to attempt to translate to FetchXML</param>
        /// <param name="schema">The schema of the node that the criteria apply to</param>
        /// <param name="allowedPrefix">The prefix of the table that the <paramref name="criteria"/> can be translated for, or <c>null</c> if any tables can be referenced</param>
        /// <param name="targetEntityName">The logical name of the root entity that the FetchXML query is targetting</param>
        /// <param name="targetEntityAlias">The alias of the root entity that the FetchXML query is targetting</param>
        /// <param name="items">The child items of the root entity in the FetchXML query</param>
        /// <param name="filter">The FetchXML version of the <paramref name="criteria"/> that is generated by this method when it covers multiple conditions</param>
        /// <param name="condition">The FetchXML version of the <paramref name="criteria"/> that is generated by this method when it is for a single condition only</param>
        /// <returns><c>true</c> if the <paramref name="criteria"/> can be translated to FetchXML, or <c>false</c> otherwise</returns>
        private bool TranslateFetchXMLCriteria(NodeCompilationContext context, IAttributeMetadataCache metadata, BooleanExpression criteria, INodeSchema schema, string allowedPrefix, string targetEntityName, string targetEntityAlias, object[] items, out condition condition, out filter filter)
        {
            condition = null;
            filter = null;

            if (criteria is BooleanBinaryExpression binary)
            {
                if (!TranslateFetchXMLCriteria(context, metadata, binary.FirstExpression, schema, allowedPrefix, targetEntityName, targetEntityAlias, items, out var lhsCondition, out var lhsFilter))
                    return false;
                if (!TranslateFetchXMLCriteria(context, metadata, binary.SecondExpression, schema, allowedPrefix, targetEntityName, targetEntityAlias, items, out var rhsCondition, out var rhsFilter))
                    return false;

                filter = new filter
                {
                    type = binary.BinaryExpressionType == BooleanBinaryExpressionType.And ? filterType.and : filterType.or,
                    Items = new[]
                    {
                        (object) lhsCondition ?? lhsFilter,
                        (object) rhsCondition ?? rhsFilter
                    }
                };
                return true;
            }

            if (criteria is BooleanParenthesisExpression paren)
            {
                return TranslateFetchXMLCriteria(context, metadata, paren.Expression, schema, allowedPrefix, targetEntityName, targetEntityAlias, items, out condition, out filter);
            }

            if (criteria is BooleanComparisonExpression comparison)
            {
                // Handle most comparison operators (=, <> etc.)
                // Comparison can be between a column and either a literal value, function call or another column (for joins only)
                // Function calls are used to represent more complex FetchXML query operators
                // Operands could be in either order, so `column = 'value'` or `'value' = column` should both be allowed
                var field = comparison.FirstExpression as ColumnReferenceExpression;
                var literal = comparison.SecondExpression as Literal;
                var func = comparison.SecondExpression as FunctionCall;
                var field2 = comparison.SecondExpression as ColumnReferenceExpression;
                var variable = comparison.SecondExpression as VariableReference;
                var parameterless = comparison.SecondExpression as ParameterlessCall;
                var globalVariable = comparison.SecondExpression as GlobalVariableExpression;
                var expr = comparison.SecondExpression;
                var type = comparison.ComparisonType;

                if (field != null && field2 != null)
                {
                    // The operator is comparing two attributes. This is allowed in join criteria,
                    // but not in filter conditions before version 9.1.0.19251
                    if (!context.Options.ColumnComparisonAvailable)
                        return false;
                }

                // If we couldn't find the pattern `column = value` or `column = func()`, try looking in the opposite order
                if (field == null && literal == null && func == null && variable == null && parameterless == null && globalVariable == null)
                {
                    field = comparison.SecondExpression as ColumnReferenceExpression;
                    literal = comparison.FirstExpression as Literal;
                    func = comparison.FirstExpression as FunctionCall;
                    variable = comparison.FirstExpression as VariableReference;
                    parameterless = comparison.FirstExpression as ParameterlessCall;
                    globalVariable = comparison.FirstExpression as GlobalVariableExpression;
                    expr = comparison.FirstExpression;
                    field2 = null;

                    // Switch the operator depending on the order of the column and value, so `column > 3` uses gt but `3 > column` uses le
                    switch (type)
                    {
                        case BooleanComparisonType.GreaterThan:
                            type = BooleanComparisonType.LessThan;
                            break;

                        case BooleanComparisonType.GreaterThanOrEqualTo:
                            type = BooleanComparisonType.LessThanOrEqualTo;
                            break;

                        case BooleanComparisonType.LessThan:
                            type = BooleanComparisonType.GreaterThan;
                            break;

                        case BooleanComparisonType.LessThanOrEqualTo:
                            type = BooleanComparisonType.GreaterThanOrEqualTo;
                            break;
                    }
                }

                var expressionContext = new ExpressionCompilationContext(context, schema, null);

                // If we still couldn't find the column name and value, this isn't a pattern we can support in FetchXML
                if (field == null || (literal == null && func == null && variable == null && parameterless == null && globalVariable == null && (field2 == null || !context.Options.ColumnComparisonAvailable) && !expr.IsConstantValueExpression(expressionContext, out literal)))
                    return false;

                // Select the correct FetchXML operator
                @operator op;

                switch (type)
                {
                    case BooleanComparisonType.Equals:
                        op = @operator.eq;
                        break;

                    case BooleanComparisonType.GreaterThan:
                        op = @operator.gt;
                        break;

                    case BooleanComparisonType.GreaterThanOrEqualTo:
                        op = @operator.ge;
                        break;

                    case BooleanComparisonType.LessThan:
                        op = @operator.lt;
                        break;

                    case BooleanComparisonType.LessThanOrEqualTo:
                        op = @operator.le;
                        break;

                    case BooleanComparisonType.NotEqualToBrackets:
                    case BooleanComparisonType.NotEqualToExclamation:
                        op = @operator.ne;
                        break;

                    default:
                        throw new NotSupportedQueryFragmentException("Unsupported comparison type", comparison);
                }

                ValueExpression[] values = null;

                if (literal != null)
                {
                    values = new[] { literal };
                }
                else if (variable != null)
                {
                    values = new[] { variable };
                }
                else if (globalVariable != null)
                {
                    values = new[] { globalVariable };
                }
                else if (func != null && Enum.TryParse<@operator>(func.FunctionName.Value.ToLower(), out var customOperator))
                {
                    if (op == @operator.eq)
                    {
                        // If we've got the pattern `column = func()`, select the FetchXML operator from the function name
                        op = customOperator;

                        // Check for unsupported SQL DOM elements within the function call
                        if (func.CallTarget != null)
                            throw new NotSupportedQueryFragmentException("Unsupported function call target", func);

                        if (func.Collation != null)
                            throw new NotSupportedQueryFragmentException("Unsupported function collation", func);

                        if (func.OverClause != null)
                            throw new NotSupportedQueryFragmentException("Unsupported function OVER clause", func);

                        if (func.UniqueRowFilter != UniqueRowFilter.NotSpecified)
                            throw new NotSupportedQueryFragmentException("Unsupported function unique filter", func);

                        if (func.WithinGroupClause != null)
                            throw new NotSupportedQueryFragmentException("Unsupported function group clause", func);

                        if (func.Parameters.Count > 1 && op != @operator.containvalues && op != @operator.notcontainvalues)
                            throw new NotSupportedQueryFragmentException("Unsupported number of function parameters", func);

                        // Some advanced FetchXML operators use a value as well - take this as the function parameter
                        // This provides support for queries such as `createdon = lastxdays(3)` becoming <condition attribute="createdon" operator="last-x-days" value="3" />
                        if (op == @operator.containvalues || op == @operator.notcontainvalues ||
                            ((op == @operator.infiscalperiodandyear || op == @operator.inorafterfiscalperiodandyear || op == @operator.inorbeforefiscalperiodandyear) && func.Parameters.Count == 2))
                        {
                            var nonLiteral = func.Parameters.FirstOrDefault(funcParam => !(funcParam is Literal));

                            if (nonLiteral != null)
                                throw new NotSupportedQueryFragmentException("Unsupported function parameter", nonLiteral);

                            values = func.Parameters.Cast<Literal>().ToArray();
                        }
                        else if (func.Parameters.Count == 1)
                        {
                            if (!(func.Parameters[0] is Literal paramLiteral))
                                throw new NotSupportedQueryFragmentException("Unsupported function parameter", func.Parameters[0]);

                            values = new[] { paramLiteral };
                        }
                    }
                    else
                    {
                        // Can't use functions with other operators
                        throw new NotSupportedQueryFragmentException("Unsupported function use. Only <field> = <func>(<param>) usage is supported", comparison);
                    }
                }
                else if (func != null)
                {
                    if (func.IsConstantValueExpression(expressionContext, out literal))
                        values = new[] { literal };
                    else
                        return false;
                }
                else if (parameterless != null)
                {
                    if (parameterless.IsConstantValueExpression(expressionContext, out literal))
                    {
                        values = new[] { literal };
                    }
                    else if (parameterless.ParameterlessCallType != ParameterlessCallType.CurrentTimestamp && op == @operator.eq)
                    {
                        op = @operator.equserid;
                    }
                    else if (parameterless.ParameterlessCallType != ParameterlessCallType.CurrentTimestamp && op == @operator.ne)
                    {
                        op = @operator.neuserid;
                    }
                    else
                    {
                        return false;
                    }
                }

                // Find the entity that the condition applies to, which may be different to the entity that the condition FetchXML element will be 
                // added within
                var columnName = field.GetColumnName();
                if (!schema.ContainsColumn(columnName, out columnName))
                    return false;

                var parts = columnName.Split('.');

                if (parts.Length != 2)
                    return false;

                var entityAlias = parts[0];
                var attrName = parts[1];

                if (allowedPrefix != null && !allowedPrefix.Equals(entityAlias))
                    return false;

                var entityName = AliasToEntityName(targetEntityAlias, targetEntityName, items, entityAlias);

                if (IsInvalidAuditFilter(targetEntityName, entityName, items))
                    return false;

                var meta = metadata[entityName];

                if (field2 == null)
                {
                    return TranslateFetchXMLCriteriaWithVirtualAttributes(context, meta, entityAlias, attrName, op, values, metadata, targetEntityAlias, items, out condition, out filter);
                }
                else
                {
                    // Column comparisons can only happen within a single entity
                    var columnName2 = field2.GetColumnName();
                    if (!schema.ContainsColumn(columnName2, out columnName2))
                        return false;

                    var parts2 = columnName2.Split('.');
                    var entityAlias2 = parts2[0];
                    var attrName2 = parts2[1];

                    if (!entityAlias.Equals(entityAlias2, StringComparison.OrdinalIgnoreCase))
                        return false;

                    var attr1 = meta.Attributes.SingleOrDefault(a => a.LogicalName.Equals(attrName, StringComparison.OrdinalIgnoreCase));
                    var attr2 = meta.Attributes.SingleOrDefault(a => a.LogicalName.Equals(attrName2, StringComparison.OrdinalIgnoreCase));

                    if (!String.IsNullOrEmpty(attr1?.AttributeOf))
                        return false;

                    if (!String.IsNullOrEmpty(attr2?.AttributeOf))
                        return false;

                    condition = new condition
                    {
                        entityname = StandardizeAlias(entityAlias, targetEntityAlias, items),
                        attribute = RemoveAttributeAlias(attrName.ToLowerInvariant(), entityAlias, targetEntityAlias, items),
                        @operator = op,
                        ValueOf = RemoveAttributeAlias(attrName2.ToLowerInvariant(), entityAlias, targetEntityAlias, items)
                    };
                    return true;
                }
            }

            if (criteria is InPredicate inPred)
            {
                // Checking if a column is in a list of literals is foldable, everything else isn't
                if (!(inPred.Expression is ColumnReferenceExpression inCol))
                    return false;

                if (inPred.Subquery != null)
                    return false;

                if (!inPred.Values.All(v => v is Literal))
                    return false;

                var columnName = inCol.GetColumnName();

                if (!schema.ContainsColumn(columnName, out columnName))
                    return false;

                var parts = columnName.Split('.');
                var entityAlias = parts[0];
                var attrName = parts[1];

                if (allowedPrefix != null && !allowedPrefix.Equals(entityAlias))
                    return false;

                var entityName = AliasToEntityName(targetEntityAlias, targetEntityName, items, entityAlias);

                if (IsInvalidAuditFilter(targetEntityName, entityName, items))
                    return false;

                var meta = metadata[entityName];

                return TranslateFetchXMLCriteriaWithVirtualAttributes(context, meta, entityAlias, attrName, inPred.NotDefined ? @operator.notin : @operator.@in, inPred.Values.Cast<Literal>().ToArray(), metadata, targetEntityAlias, items, out condition, out filter);
            }

            if (criteria is BooleanIsNullExpression isNull)
            {
                if (!(isNull.Expression is ColumnReferenceExpression nullCol))
                    return false;

                var columnName = nullCol.GetColumnName();

                if (!schema.ContainsColumn(columnName, out columnName))
                    return false;

                var parts = columnName.Split('.');
                var entityAlias = parts[0];
                var attrName = parts[1];

                if (allowedPrefix != null && !allowedPrefix.Equals(entityAlias))
                    return false;

                var entityName = AliasToEntityName(targetEntityAlias, targetEntityName, items, entityAlias);

                if (IsInvalidAuditFilter(targetEntityName, entityName, items))
                    return false;

                var meta = metadata[entityName];

                return TranslateFetchXMLCriteriaWithVirtualAttributes(context, meta, entityAlias, attrName, isNull.IsNot ? @operator.notnull : @operator.@null, null, metadata, targetEntityAlias, items, out condition, out filter);
            }

            if (criteria is LikePredicate like)
            {
                if (!(like.FirstExpression is ColumnReferenceExpression col))
                    return false;

                if (!(like.SecondExpression is StringLiteral value))
                    return false;

                if (like.EscapeExpression != null)
                    return false;

                var columnName = col.GetColumnName();

                if (!schema.ContainsColumn(columnName, out columnName))
                    return false;

                var parts = columnName.Split('.');
                var entityAlias = parts[0];
                var attrName = parts[1];

                if (allowedPrefix != null && !allowedPrefix.Equals(entityAlias))
                    return false;

                var entityName = AliasToEntityName(targetEntityAlias, targetEntityName, items, entityAlias);

                if (IsInvalidAuditFilter(targetEntityName, entityName, items))
                    return false;

                var meta = metadata[entityName];

                return TranslateFetchXMLCriteriaWithVirtualAttributes(context, meta, entityAlias, attrName, like.NotDefined ? @operator.notlike : @operator.like, new[] { value }, metadata, targetEntityAlias, items, out condition, out filter);
            }

            if (criteria is FullTextPredicate ||
                criteria is BooleanNotExpression not && not.Expression is FullTextPredicate)
            {
                var fullText = criteria as FullTextPredicate;
                not = criteria as BooleanNotExpression;

                if (fullText == null)
                    fullText = not.Expression as FullTextPredicate;

                if (fullText.FullTextFunctionType != FullTextFunctionType.Contains)
                    return false;

                if (fullText.Columns.Count != 1)
                    return false;

                if (fullText.Columns[0].ColumnType != ColumnType.Regular)
                    return false;

                if (!(fullText.Value is StringLiteral value))
                    return false;

                var valueParts = value.Value.ToUpperInvariant().Split(new[] { " OR " }, StringSplitOptions.None);
                if (valueParts.Any(p => !Int32.TryParse(p, out _)))
                    return false;

                var columnName = fullText.Columns[0].GetColumnName();

                if (!schema.ContainsColumn(columnName, out columnName))
                    return false;

                var parts = columnName.Split('.');
                var entityAlias = parts[0];
                var attrName = parts[1];

                if (allowedPrefix != null && !allowedPrefix.Equals(entityAlias))
                    return false;

                var entityName = AliasToEntityName(targetEntityAlias, targetEntityName, items, entityAlias);

                if (IsInvalidAuditFilter(targetEntityName, entityName, items))
                    return false;

                var meta = metadata[entityName];

                var attr = meta.Attributes.Single(a => a.LogicalName.Equals(attrName));
                if (!(attr is MultiSelectPicklistAttributeMetadata))
                    return false;

                return TranslateFetchXMLCriteriaWithVirtualAttributes(context, meta, entityAlias, attrName, not == null ? @operator.containvalues : @operator.notcontainvalues, valueParts.Select(v => new IntegerLiteral { Value = v }).ToArray(), metadata, targetEntityAlias, items, out condition, out filter);
            }

            return false;
        }

        /// <summary>
        /// Checks if this filter can't be folded into the FetchXML query because it's on an outer-joined table
        /// from the audit entity.
        /// </summary>
        /// <param name="targetEntityName">The base entity type of the FetchXML query</param>
        /// <param name="entityName">The entity type being filtered</param>
        /// <param name="items">The list of items in the FetchXML entity element</param>
        /// <returns><c>true</c> if the filter cannot be applied, or <c>false</c> otherwise</returns>
        /// <remarks>
        /// The audit provider does not support filtering using the &lt;condition entityname="systemuser" .../&gt; syntax.
        /// See https://github.com/MarkMpn/Sql4Cds/issues/294
        /// </remarks>
        private bool IsInvalidAuditFilter(string targetEntityName, string entityName, object[] items)
        {
            if (targetEntityName != "audit")
                return false;

            if (entityName == "audit")
                return false;

            // Audit can only have a single join.
            var join = items.OfType<FetchLinkEntityType>().Single();
            
            // Filtering on an inner-joined table works, only indicate an error if we're doing an outer join
            return join.linktype != "inner";
        }

        /// <summary>
        /// Handles special cases for virtual attributes in FetchXML conditions
        /// </summary>
        /// <param name="context">The context the query is being built in</param>
        /// <param name="meta">The metadata for the target entity the condition is for</param>
        /// <param name="entityAlias">The alias of the entity in the query the condition is for</param>
        /// <param name="attrName">The logical name of the attribute the condition is for</param>
        /// <param name="op">The condition operator to apply</param>
        /// <param name="literals">The values to compare the attribute value to</param>
        /// <param name="metadata">The <see cref="IAttributeMetadataCache"/> to use to get metadata</param>
        /// <param name="targetEntityAlias">The alias of the root entity that the FetchXML query is targetting</param>
        /// <param name="items">The child items of the root entity in the FetchXML query</param>
        /// <param name="filter">The FetchXML version of the <paramref name="criteria"/> that is generated by this method when it covers multiple conditions</param>
        /// <param name="condition">The FetchXML version of the <paramref name="criteria"/> that is generated by this method when it is for a single condition only</param>
        /// <returns><c>true</c> if the condition can be translated to FetchXML, or <c>false</c> otherwise</returns>
        private bool TranslateFetchXMLCriteriaWithVirtualAttributes(NodeCompilationContext context, EntityMetadata meta, string entityAlias, string attrName, @operator op, ValueExpression[] literals, IAttributeMetadataCache metadata, string targetEntityAlias, object[] items, out condition condition, out filter filter)
        {
            condition = null;
            filter = null;

            attrName = RemoveAttributeAlias(attrName, entityAlias, targetEntityAlias, items);

            // Handle virtual ___name and ___type attributes
            var attribute = meta.Attributes.SingleOrDefault(a => a.LogicalName.Equals(attrName, StringComparison.OrdinalIgnoreCase) && a.AttributeOf == null);
            string attributeSuffix = null;

            if (attribute == null && (attrName.EndsWith("name", StringComparison.OrdinalIgnoreCase) || attrName.EndsWith("type", StringComparison.OrdinalIgnoreCase)))
            {
                attribute = meta.Attributes.SingleOrDefault(a => a.LogicalName.Equals(attrName.Substring(0, attrName.Length - 4), StringComparison.OrdinalIgnoreCase) && a.AttributeOf == null);

                if (attribute != null)
                {
                    attributeSuffix = attrName.Substring(attrName.Length - 4).ToLower();
                    attrName = attribute.LogicalName;
                }
            }

            // Can't fold LIKE queries for non-string fields - the server will try to convert the value to the type of
            // the attribute (e.g. integer) and throw an exception. 
            if (attribute != null && attributeSuffix == null && (op == @operator.like || op == @operator.notlike) && !(attribute.AttributeType == AttributeTypeCode.String || attribute.AttributeType == AttributeTypeCode.Memo))
                return false;

            // Can't fold queries on PartyList attributes
            if (attribute != null && attribute.AttributeType == AttributeTypeCode.PartyList)
                return false;

            var values = literals == null ? null : literals
                .Select(lit => new conditionValue
                {
                    Value = lit is Literal lit1
                    ? lit1.Value
                    : lit is VariableReference var1
                        ? var1.Name
                        : lit is GlobalVariableExpression glob
                            ? glob.Name
                            : null,
                    IsVariable = lit is VariableReference || lit is GlobalVariableExpression
                })
                .ToArray();

            var value = values == null || values.Length != 1 ? null : values[0].Value;
            var isVariable = values == null || values.Length != 1 ? false : values[0].IsVariable;

            var usesItems = values != null && values.Length > 1 || op == @operator.@in || op == @operator.notin || op == @operator.containvalues || op == @operator.notcontainvalues;

            if (attribute is DateTimeAttributeMetadata && literals != null &&
                (op == @operator.eq || op == @operator.ne || op == @operator.neq || op == @operator.gt || op == @operator.ge || op == @operator.lt || op == @operator.le || op == @operator.@in || op == @operator.notin))
            {
                for (var i = 0; i < literals.Length; i++)
                {
                    if (!(literals[i] is Literal lit))
                        continue;

                    try
                    {
                        DateTime dt;

                        if (lit is StringLiteral)
                            dt = SqlDateTime.Parse(lit.Value).Value;
                        else if (lit is IntegerLiteral || lit is NumericLiteral || lit is RealLiteral)
                            dt = new DateTime(1900, 1, 1).AddDays(Double.Parse(lit.Value, CultureInfo.InvariantCulture));
                        else
                            throw new NotSupportedQueryFragmentException("Invalid datetime value", lit);

                        DateTimeOffset dto;

                        if (context.Options.UseLocalTimeZone)
                            dto = new DateTimeOffset(dt, TimeZoneInfo.Local.GetUtcOffset(dt));
                        else
                            dto = new DateTimeOffset(dt, TimeSpan.Zero);

                        var formatted = dto.ToString("yyyy-MM-ddTHH':'mm':'ss.FFFzzz");

                        if (literals.Length == 1)
                            value = formatted;

                        values[i].Value = formatted;
                    }
                    catch (FormatException)
                    {
                        throw new NotSupportedQueryFragmentException("Invalid datetime value", lit);
                    }
                }
            }

            if (attribute != null && attributeSuffix != null)
            {
                var virtualAttributeHandled = false;

                // If filtering on the display name of an optionset attribute, convert it to filtering on the underlying value field
                // instead where possible.
                if (attributeSuffix == "name" && attribute is EnumAttributeMetadata enumAttr &&
                    (op == @operator.eq || op == @operator.ne || op == @operator.neq || op == @operator.@in || op == @operator.notin))
                {
                    for (var i = 0; i < literals.Length; i++)
                    {
                        if (!(literals[i] is Literal))
                            continue;

                        var matchingOptions = enumAttr.OptionSet.Options.Where(o => o.Label.UserLocalizedLabel.Label.Equals(values[i].Value, StringComparison.InvariantCultureIgnoreCase)).ToList();

                        if (matchingOptions.Count == 1)
                        {
                            values[i] = new conditionValue { Value = matchingOptions[0].Value.ToString() };
                            virtualAttributeHandled = true;
                        }
                        else if (matchingOptions.Count == 0)
                        {
                            throw new NotSupportedQueryFragmentException("Unknown optionset value", literals[i]) { Suggestion = "Supported values are:\r\n" + String.Join("\r\n", enumAttr.OptionSet.Options.Select(o => "* " + o.Label.UserLocalizedLabel.Label)) };
                        }
                    }

                    value = values[0].Value;
                }

                // Same again for boolean attributes
                if (attributeSuffix == "name" && attribute is BooleanAttributeMetadata boolAttr &&
                    (op == @operator.eq || op == @operator.ne || op == @operator.neq || op == @operator.@in || op == @operator.notin))
                {
                    for (var i = 0; i < literals.Length; i++)
                    {
                        if (!(literals[i] is Literal))
                            continue;

                        if (boolAttr.OptionSet.TrueOption.Label.UserLocalizedLabel.Label.Equals(values[i].Value, StringComparison.InvariantCultureIgnoreCase))
                        {
                            values[i] = new conditionValue { Value = "1" };
                            virtualAttributeHandled = true;
                        }
                        else if (boolAttr.OptionSet.FalseOption.Label.UserLocalizedLabel.Label.Equals(values[i].Value, StringComparison.InvariantCultureIgnoreCase))
                        {
                            values[i] = new conditionValue { Value = "0" };
                            virtualAttributeHandled = true;
                        }
                        else
                        {
                            throw new NotSupportedQueryFragmentException("Unknown optionset value", literals[i]) { Suggestion = "Supported values are:\r\n* " + boolAttr.OptionSet.FalseOption.Label.UserLocalizedLabel.Label + "\r\n* " + boolAttr.OptionSet.TrueOption.Label.UserLocalizedLabel.Label };
                        }
                    }

                    value = values[0].Value;
                }

                if (attribute is LookupAttributeMetadata lookupAttr)
                {
                    // Check the real name of the underlying virtual attribute. We use the consistent suffixes "name" and "type" but
                    // it's not always the same under the hood.
                    if (attributeSuffix == "name")
                    {
                        attribute = meta.Attributes
                            .OfType<StringAttributeMetadata>()
                            .SingleOrDefault(a => a.AttributeOf == attrName && a.AttributeType == AttributeTypeCode.String && a.YomiOf == null);
                    }
                    else
                    {
                        attribute = meta.Attributes
                            .SingleOrDefault(a => a.AttributeOf == attrName && a.AttributeType == AttributeTypeCode.EntityName);
                    }

                    if (attribute != null)
                    {
                        attrName = attribute.LogicalName;

                        if (attributeSuffix == "name")
                        {
                            virtualAttributeHandled = true;
                        }
                        else
                        {
                            // Type attributes can only handle a limited set of operators
                            if (op == @operator.@null || op == @operator.notnull || op == @operator.eq || op == @operator.ne || op == @operator.@in || op == @operator.notin)
                            {
                                virtualAttributeHandled = true;
                            }
                        }
                    }
                }

                if (!virtualAttributeHandled)
                    return false;
            }

            if (values != null && attribute?.AttributeType == AttributeTypeCode.EntityName)
            {
                // Filtering on entity name attributes is only possible for a limited set of operators. Values should be
                // presented as object type codes rather than logical names
                if (op != @operator.eq && op != @operator.ne && op != @operator.neq && op != @operator.@in && op != @operator.notin)
                    return false;

                for (var i = 0; i < values.Length; i++)
                {
                    if (values[i].IsVariable)
                    {
                        // Variables must be an integer type
                        var variableType = context.ParameterTypes[values[i].Value].ToNetType(out _);

                        if (variableType != typeof(SqlInt32))
                            return false;
                    }
                    else if (!Int32.TryParse(values[i].Value, out _))
                    {
                        try
                        {
                            // Convert the literal entity name to the object type code
                            var targetMetadata = metadata[values[i].Value];
                            values[i].Value = targetMetadata.ObjectTypeCode?.ToString();
                        }
                        catch
                        {
                            // If the entity name does not exist, use a placeholder value which can't match any real entity type
                            values[i].Value = "0";
                        }
                    }
                }

                value = values[0].Value;
            }

            condition = new condition
            {
                entityname = StandardizeAlias(entityAlias, targetEntityAlias, items),
                attribute = attrName.ToLowerInvariant(),
                @operator = op,
                value = usesItems ? null : value,
                Items = usesItems ? values : null,
                IsVariable = isVariable
            };

            // Filtering on "solution" entity via an outer join seems to generate an error:
            // https://github.com/MarkMpn/Sql4Cds/issues/309
            var linkName = condition.entityname;
            if (meta.LogicalName == "solution" && linkName != null && !new FetchEntityType { Items = items }.GetLinkEntities(true).Any(link => link.alias == linkName))
                return false;

            if (op == @operator.ne || op == @operator.nebusinessid || op == @operator.neq || op == @operator.neuserid)
            {
                // FetchXML not-equal type operators treat NULL as not-equal to values, but T-SQL treats them as not-not-equal. Add
                // an extra not-null condition to keep it compatible with T-SQL
                filter = new filter
                {
                    type = filterType.and,
                    Items = new[]
                    {
                        condition,
                        new condition
                        {
                            entityname = condition.entityname,
                            attribute = condition.attribute,
                            @operator = @operator.notnull
                        }
                    }
                };
                condition = null;
            }

            return true;
        }

        /// <summary>
        /// Attribute name may actually be an alias - convert it to the underlying attribute name
        /// </summary>
        /// <param name="attrName">The attribute name to convert</param>
        /// <param name="entityAlias">The alias of the entity containing the attribute</param>
        /// <param name="targetEntityAlias">The alias of the root entity in the FetchXML query</param>
        /// <param name="items">The child items in the root entity object</param>
        /// <returns>The underlying attribute name</returns>
        private string RemoveAttributeAlias(string attrName, string entityAlias, string targetEntityAlias, object[] items)
        {
            if (entityAlias != targetEntityAlias)
            {
                var entity = new FetchEntityType { Items = items };
                var linkEntity = entity.FindLinkEntity(entityAlias);
                items = linkEntity.Items;
            }

            if (items == null)
                return attrName;

            var attribute = items.OfType<FetchAttributeType>().SingleOrDefault(a => a.alias != null && a.alias.Equals(attrName, StringComparison.OrdinalIgnoreCase));

            if (attribute != null)
                return attribute.name;

            return attrName;
        }

        /// <summary>
        /// Gets the alias to use for an entity or link-entity in the entityname property of a FetchXML condition
        /// </summary>
        /// <param name="entityAlias">The alias of the table that the condition refers to</param>
        /// <param name="targetEntityAlias">The alias of the root entity in the FetchXML query</param>
        /// <param name="items">The child items in the root entity object</param>
        /// <returns>The entityname to use in the FetchXML condition</returns>
        private string StandardizeAlias(string entityAlias, string targetEntityAlias, object[] items)
        {
            if (entityAlias.Equals(targetEntityAlias, StringComparison.OrdinalIgnoreCase))
                return null;

            var entity = new FetchEntityType { Items = items };
            var linkEntity = entity.FindLinkEntity(entityAlias);

            return linkEntity?.alias ?? entityAlias;
        }

        /// <summary>
        /// Gets the logical name of an entity from the alias of a table
        /// </summary>
        /// <param name="targetEntityAlias">The alias of the root entity in the FetchXML query</param>
        /// <param name="targetEntityName">The logical name of the root entity in the FetchXML query</param>
        /// <param name="items">The child items in the root entity object</param>
        /// <param name="alias">The alias of the table to get the logical name for</param>
        /// <returns>The logical name of the aliased entity</returns>
        private string AliasToEntityName(string targetEntityAlias, string targetEntityName, object[] items, string alias)
        {
            if (targetEntityAlias.Equals(alias, StringComparison.OrdinalIgnoreCase))
                return targetEntityName;

            var entity = new FetchEntityType { Items = items };
            var linkEntity = entity.FindLinkEntity(alias);

            return linkEntity.name;
        }

        /// <summary>
        /// Gets the variables that are in use by this node and optionally its sources
        /// </summary>
        /// <param name="recurse">Indicates if the returned list should include the variables used by the sources of this node</param>
        /// <returns>A sequence of variables names that are in use by this node</returns>
        public IEnumerable<string> GetVariables(bool recurse)
        {
            var variables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (recurse)
            {
                foreach (var source in GetSources().OfType<IDataExecutionPlanNodeInternal>())
                    variables.UnionWith(source.GetVariables(true));
            }

            variables.UnionWith(GetVariablesInternal());

            return variables;
        }

        /// <summary>
        /// Gets the variables that are in use by this node
        /// </summary>
        /// <returns>A sequence of variables names that are in use by this node</returns>
        protected virtual IEnumerable<string> GetVariablesInternal()
        {
            return Array.Empty<string>();
        }

        public abstract object Clone();
    }
}
