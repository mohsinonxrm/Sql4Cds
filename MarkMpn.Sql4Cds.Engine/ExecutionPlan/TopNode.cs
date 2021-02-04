﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Xrm.Sdk;

namespace MarkMpn.Sql4Cds.Engine.ExecutionPlan
{
    public class TopNode : BaseNode
    {
        public float Top { get; set; }

        public bool Percent { get; set; }

        public bool WithTies { get; set; }

        public IExecutionPlanNode Source { get; set; }

        public override IEnumerable<Entity> Execute(IOrganizationService org, IAttributeMetadataCache metadata, IQueryExecutionOptions options)
        {
            if (WithTies)
                throw new NotImplementedException();

            if (!Percent)
            {
                return Source.Execute(org, metadata, options)
                    .Take((int)Top);
            }
            else
            {
                var count = Source.Execute(org, metadata, options).Count();
                var top = count * Top / 100;

                return Source.Execute(org, metadata, options)
                    .Take((int)top);
            }
        }

        public override IEnumerable<string> GetRequiredColumns()
        {
            return Array.Empty<string>();
        }

        public override NodeSchema GetSchema(IAttributeMetadataCache metadata)
        {
            return Source.GetSchema(metadata);
        }

        public override IEnumerable<IExecutionPlanNode> GetSources()
        {
            yield return Source;
        }
    }
}