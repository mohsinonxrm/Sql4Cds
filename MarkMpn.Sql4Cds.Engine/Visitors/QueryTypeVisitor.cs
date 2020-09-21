﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace MarkMpn.Sql4Cds.Engine.Visitors
{
    class QueryTypeVisitor : TSqlFragmentVisitor
    {
        public bool IsData { get; private set; }

        public bool IsGlobalOptionSet { get; private set; }

        public bool IsEntityMetadata { get; private set; }

        public bool IsMetadata { get; private set; }

        public override void ExplicitVisit(NamedTableReference node)
        {
            base.ExplicitVisit(node);

            if (node.SchemaObject.BaseIdentifier.Value.Equals("globaloptionset", StringComparison.OrdinalIgnoreCase))
            {
                IsGlobalOptionSet = true;
                IsMetadata = true;
            }
            else if (node.SchemaObject.BaseIdentifier.Value.Equals("entity", StringComparison.OrdinalIgnoreCase) ||
                node.SchemaObject.BaseIdentifier.Value.Equals("attribute", StringComparison.OrdinalIgnoreCase) ||
                node.SchemaObject.BaseIdentifier.Value.Equals("relationship_1_n", StringComparison.OrdinalIgnoreCase) ||
                node.SchemaObject.BaseIdentifier.Value.Equals("relationship_n_n", StringComparison.OrdinalIgnoreCase))
            {
                IsEntityMetadata = true;
                IsMetadata = true;
            }
            else if (node.SchemaObject.BaseIdentifier.Value.Equals("label", StringComparison.OrdinalIgnoreCase) ||
                node.SchemaObject.BaseIdentifier.Value.Equals("localizedlabel", StringComparison.OrdinalIgnoreCase) ||
                node.SchemaObject.BaseIdentifier.Value.Equals("option", StringComparison.OrdinalIgnoreCase))
            {
                IsMetadata = true;
            }
            else
            {
                IsData = true;
            }
        }
    }
}
