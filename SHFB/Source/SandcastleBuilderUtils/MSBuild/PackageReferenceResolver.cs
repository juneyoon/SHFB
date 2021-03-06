﻿//===============================================================================================================
// System  : Sandcastle Help File Builder MSBuild Tasks
// File    : PackageReferenceResolver.cs
// Author  : Eric Woodruff  (Eric@EWoodruff.us)
// Updated : 05/20/2019
// Note    : Copyright 2017-2019, Eric Woodruff, All rights reserved
// Compiler: Microsoft Visual C#
//
// This file contains the class used to resolve PackageReference elements in MSBuild project files
//
// This code is published under the Microsoft Public License (Ms-PL).  A copy of the license should be
// distributed with the code and can be found at the project website: https://GitHub.com/EWSoftware/SHFB.  This
// notice, the author's name, and all copyright notices must remain intact in all applications, documentation,
// and source files.
//
//    Date     Who  Comments
// ==============================================================================================================
// 04/20/2017  EFW  Created the code
//===============================================================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.Build.Evaluation;
using Newtonsoft.Json.Linq;
using SandcastleBuilder.Utils.BuildEngine;

namespace SandcastleBuilder.Utils.MSBuild
{
    /// <summary>
    /// This is used to resolved package references (<c>PackageReference</c> elements) in MSBuild project files
    /// </summary>
    /// <remarks>Package references are handled by the NuGet MSBuild targets.  Trying to figure out how they
    /// work would be quite difficult as would trying to plug them into the reflection data generation project.
    /// However, those tasks create an asset file that contains all of the information we need so we use it to
    /// figure out the reference assembly locations along with any dependencies.</remarks>
    public class PackageReferenceResolver
    {
        #region Private data members
        //=====================================================================

        private BuildProcess buildProcess;
        private HashSet<string> resolvedDependencies, packageReferences;
        private JToken packages;
        private string projectFilename;
        private string[] nugetPackageFolders;

        #endregion

        #region Properties
        //=====================================================================

        /// <summary>
        /// This returns a list of all resolved package reference assemblies along with all dependency assembly
        /// references.
        /// </summary>
        public IEnumerable<string> ReferenceAssemblies
        {
            get
            {
                if(packages != null || packageReferences.Count != 0 && nugetPackageFolders != null)
                    foreach(string assembly in this.ResolvePackageReferencesInternal(packageReferences))
                        foreach(string folder in nugetPackageFolders)
                        {
                            string path = Path.Combine(folder, assembly.Replace("/", @"\"));

                            if(File.Exists(path))
                                yield return path;
                        }
            }
        }
        #endregion

        #region Constructor
        //=====================================================================

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="buildProcess">The build process that will make use of the resolver</param>
        public PackageReferenceResolver(BuildProcess buildProcess)
        {
            this.buildProcess = buildProcess;

            resolvedDependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            packageReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        #endregion

        #region Helper methods
        //=====================================================================

        /// <summary>
        /// This is used to load the package reference information from the given project
        /// </summary>
        /// <param name="project">The MSBuild project from which to get the package references</param>
        /// <returns>True if package reference info was loaded, false if not</returns>
        public bool LoadPackageReferenceInfo(Project project)
        {
            packages = null;
            resolvedDependencies.Clear();
            packageReferences.Clear();

            try
            {
                projectFilename = project.FullPath;

                if(project.GetPropertyValue("NuGetProjectStyle") == "PackageReference")
                {
                    string assetFile = project.GetPropertyValue("ProjectAssetsFile");

                    if(!String.IsNullOrWhiteSpace(assetFile) && File.Exists(assetFile))
                    {
                        var packageInfo = JObject.Parse(File.ReadAllText(assetFile));
                        var targets = packageInfo["targets"];

                        if(targets != null)
                        {
                            packages = ((JProperty)targets.First()).Value;

                            string folders = project.GetPropertyValue("NuGetPackageFolders");

                            if(!String.IsNullOrWhiteSpace(folders))
                                nugetPackageFolders = folders.Split(';');
                            else
                                nugetPackageFolders = new string[0];

                            foreach(var reference in project.GetItems("PackageReference"))
                            {
                                var version = reference.Metadata.FirstOrDefault(m => m.Name == "Version");

                                if(version != null)
                                    packageReferences.Add(reference.EvaluatedInclude + "/" + version.EvaluatedValue);
                            }
                        }
                        else
                            buildProcess.ReportWarning("BE0011", "Unable to load package reference information " +
                                "for '{0}'.  Reason: Unable to locate targets element in project assets file '{1}'.",
                                projectFilename, assetFile);
                    }
                    else
                        buildProcess.ReportWarning("BE0011", "Unable to load package reference information " +
                            "for '{0}'.  Reason: Project assets file '{1}' does not exist.", projectFilename,
                            assetFile);
                }
            }
            catch(Exception ex)
            {
                // We won't prevent the build from continuing if there's an error.  We'll just report it.
                System.Diagnostics.Debug.WriteLine(ex);

                buildProcess.ReportWarning("BE0011", "Unable to load package reference information for '{0}'.  " +
                    "Reason: {1}", projectFilename, ex.Message);
            }

            return (packages != null && packageReferences.Count != 0);
        }

        /// <summary>
        /// This is used to resolve package references by looking up the package IDs in the asset file created
        /// by the NuGet Restore task.
        /// </summary>
        /// <param name="referencesToResolve">The package references to resolve</param>
        /// <returns>An enumerable list of assembly names.</returns>
        /// <remarks>If a package has dependencies, those will be resolved and returned as well</remarks>
        private IEnumerable<string> ResolvePackageReferencesInternal(IEnumerable<string> referencesToResolve)
        {
            HashSet<string> references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach(var p in referencesToResolve)
                try
                {
                    string packageName = p;

                    resolvedDependencies.Add(packageName);

                    var match = packages[packageName];

                    // If we don't get a match, try it with ".0" on the end.  Sometimes the reference version
                    // leaves it off.
                    if(match == null)
                    {
                        packageName += ".0";
                        match = packages[packageName];

                        if(match != null)
                            resolvedDependencies.Add(packageName);
                    }

                    if(match != null)
                    {
                        var assemblyInfo = match["compile"] ?? match["runtime"];

                        if(assemblyInfo != null)
                        {
                            foreach(string assemblyName in assemblyInfo.Select(a => ((JProperty)a).Name))
                            {
                                // Ignore mscorlib.dll as it's types will have been redirected elsewhere so we
                                // don't need it.  "_._" occurs in the framework SDK packages and we can ignore
                                // it too.
                                if(!assemblyName.EndsWith("/mscorlib.dll", StringComparison.OrdinalIgnoreCase) &&
                                  !assemblyName.EndsWith("_._", StringComparison.Ordinal))
                                {
                                    references.Add(Path.Combine(packageName, assemblyName));
                                }
                            }

                            var dependencies = match["dependencies"];

                            if(dependencies != null)
                            {
                                var deps = dependencies.Cast<JProperty>().Select(
                                    t => t.Name + "/" + t.ToObject<string>()).Where(
                                        d => !resolvedDependencies.Contains(d)).ToList();

                                if(deps.Count != 0)
                                {
                                    // Track the ones we've seen to prevent getting stuck due to circular
                                    // references.
                                    resolvedDependencies.UnionWith(deps);
                                    references.UnionWith(this.ResolvePackageReferencesInternal(deps));
                                }
                            }
                        }
                    }
                }
                catch(Exception ex)
                {
                    // We won't prevent the build from continuing if there's an error.  We'll just report it.
                    System.Diagnostics.Debug.WriteLine(ex);

                    buildProcess.ReportWarning("BE0011", "Unable to load package reference information for " +
                        "'{0}' in '{1}'.  Reason: {2}", p, projectFilename, ex.Message);
                }

            return references;
        }
        #endregion
    }
}
