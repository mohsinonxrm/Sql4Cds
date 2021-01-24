﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Xrm.Sdk;

namespace MarkMpn.Sql4Cds.Engine.ExecutionPlan
{
    /// <summary>
    /// Represents a node in an execution plan
    /// </summary>
    /// <remarks>
    /// Ref https://sqlserverfast.com/epr/generic_information/
    /// 
    /// Each node has Init(), GetNext() and Close(). Here this is mapped to
    /// Execute() being Init(), MoveNext() on the enumerator being GetNext()
    /// and Dispose() on the enumerator being Close()
    /// </remarks>
    interface IExecutionPlanNode
    {
        /// <summary>
        /// Executes the execution plan
        /// </summary>
        /// <param name="org">The <see cref="IOrganizationService"/> to use to execute the plan</param>
        /// <returns>A sequence of entities matched by the query</returns>
        IEnumerable<Entity> Execute(IOrganizationService org, IAttributeMetadataCache metadata, IQueryExecutionOptions options);
    }
}