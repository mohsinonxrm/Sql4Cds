﻿using System;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using XrmToolBox.Extensibility;
using XrmToolBox.Extensibility.Interfaces;

namespace MarkMpn.Sql4Cds
{
    // Do not forget to update version number and author (company attribute) in AssemblyInfo.cs class
    // To generate Base64 string for Images below, you can use https://www.base64-image.de/
    [Export(typeof(IXrmToolBoxPlugin)),
        ExportMetadata("Name", "SQL 4 CDS"),
        ExportMetadata("Description", "Query and modify data in Dataverse / D365 using standard SQL syntax"),
        // Please specify the base64 content of a 32x32 pixels image
        ExportMetadata("SmallImageBase64", "iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAIAAAD8GO2jAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAANfSURBVEhLY/z//z8DLQETlKYZwOeDDw+v3j598cy527evfvr99PP3D1BxBgFOPmkWPm1VVSMTA1NVbXl+qDg2gMWCr59ePDmy682jCUrK93j4fnz6wFWfHQIUD4w/ZWJ9/+cP1v3bNA/v0NQxfpxevrc4Nvq/uqpFSoibjbQkHzfEBGSAHkRfH+6dF1m3d/lia8fzK+dYTqj33LlOFyjuHnTR0evaqjkWLx7zx+cekZJ7BxTk5P4NJH9fenA4r6c2cv7eh19BRqACFAu+vj4+L2XlxZsMTCz/gFxJ2Q/PHwse26sOZBtb39+7SefiKflZ3c7//jJqGjwD60ACNy8tT5l/7DW6HSgWfDqy/+J9EOPMYeWD2zXCU0/0L1scGHcKKCIq8enjey6QHAPD88cC/ELfIGwUcP/SviOfoWwYwJmKFk+xLYmL2rtZ2zv8opbhk69fObi4fkGkOLh+fXwHtYwgQLGAz8ZRXxHE0DV+JCn3/vMHzusXpIHcXz9Ybl+R8Ik8Jyb50cLxtrDY1ytnZUDqGBhEJD6LSn4CIhBHUc/JhhcsjASAqQgZfHmwZ5JX6r4ezf83GIDo3zWGo1NUk5VTS8wj728WgQiuLDUHivSHeUC4EJTsNXnPgy9QU5AAzmS6YfHht/e/vnvDAxUFg8qejX//MC2dbs3F+/P2FUlmlr9AQVZdefPEIBcbRazJFMWCe8u6t0rFJplCvfn7zQO0jGZg8SAo/jSvwI9rN5W3X8uBZDQFERaI+k+n5y9+5lUWpQThQgCaBbVtdS/5gh1CEt20ZDlZocIEwZ/vjy9tnbrh8PbP4k0VragWQC2HAE5OTiD5ae2BeWsPMIjzytjr69soKiipSvCycPJysjJDVDEw/P3z/fP3359f3L734P6RixcPPv30EirDyYmeulB8AMpoUfMhWYEcoKiXtCzRShQlJlCSKbeoZdKccH1QziUdqOtFzkE3HQiwpCIguLdn0bJpJx5c+gPl4wWsegoWWRHxLihBDwdYUlGugwSES2px/fzAFMxUhJLR7i6tAeagwrKlR6+/hgoRB15fP7Qgpwiot2rpXagQDOBMRfNQU5GktAhEDQS8efpiNBXRJRW9uHX/yJkTo6loNBWRkorQAK2ajtQFONtF1AEMDACqXc2SDaY2uAAAAABJRU5ErkJggg=="),
        // Please specify the base64 content of a 80x80 pixels image
        ExportMetadata("BigImageBase64", "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"),
        ExportMetadata("BackgroundColor", "DarkMagenta"),
        ExportMetadata("PrimaryFontColor", "White"),
        ExportMetadata("SecondaryFontColor", "Gray")]
    public class Plugin : PluginBase, IPayPalPlugin
    {
        private Assembly _primaryAssembly;

        public override IXrmToolBoxPluginControl GetControl()
        {
            var controlType = Type.GetType("MarkMpn.Sql4Cds.XTB.PluginControl, MarkMpn.Sql4Cds.XTB");
            return (IXrmToolBoxPluginControl) Activator.CreateInstance(controlType, this);
        }

        /// <summary>
        /// Constructor 
        /// </summary>
        public Plugin()
        {
            AppDomain.CurrentDomain.AssemblyResolve += new ResolveEventHandler(AssemblyResolveEventHandler);
        }

        /// <summary>
        /// Event fired by CLR when an assembly reference fails to load
        /// Assumes that related assemblies will be loaded from a subfolder named the same as the Plugin
        /// For example, a folder named Sample.XrmToolBox.MyPlugin 
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="args"></param>
        /// <returns></returns>
        private Assembly AssemblyResolveEventHandler(object sender, ResolveEventArgs args)
        {
            Assembly loadAssembly = null;
            Assembly currAssembly = Assembly.GetExecutingAssembly();

            // base name of the assembly that failed to resolve
            var argName = args.Name.Split(',')[0];

            // check to see if the failing assembly is one that we reference.
            if (argName == "MarkMpn.Sql4Cds.XTB" ||
                _primaryAssembly != null && _primaryAssembly.GetReferencedAssemblies().Any(a => a.Name == argName))
            {
                // load from the path to this plugin assembly, not host executable
                string dir = Path.GetDirectoryName(currAssembly.Location).ToLower();
                string folder = Path.GetFileNameWithoutExtension(currAssembly.Location);
                dir = Path.Combine(dir, folder);

                var assmbPath = Path.Combine(dir, $"{argName}.dll");

                if (File.Exists(assmbPath))
                {
                    loadAssembly = Assembly.LoadFrom(assmbPath);
                }
                else
                {
                    throw new FileNotFoundException($"Unable to locate dependency: {assmbPath}");
                }

                if (_primaryAssembly == null && argName == "MarkMpn.Sql4Cds.XTB")
                    _primaryAssembly = loadAssembly;
            }

            return loadAssembly;
        }

        string IPayPalPlugin.DonationDescription => "SQL 4 CDS Donation";

        string IPayPalPlugin.EmailAccount => "donate@markcarrington.dev";
    }
}