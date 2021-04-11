// --------------------------------------------------------------------------------------------------------------------
// <copyright file="AssemblyInfo.cs" company="Microsoft Corporation">
//   Copyright (c) Microsoft Corporation.
// </copyright>
// <summary>
//   Assembly properties.
// </summary>
// --------------------------------------------------------------------------------------------------------------------

using System;
using System.Reflection;
using System.Runtime.InteropServices;

// General Information about an assembly is controlled through the following 
// set of attributes. Change these attribute values to modify the information
// associated with an assembly.
[assembly: AssemblyTitle("EsentCollections")]
[assembly: AssemblyDescription("")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("Microsoft")]
[assembly: AssemblyProduct("EsentCollections")]
[assembly: AssemblyCopyright("Copyright (c) Microsoft. All Rights Reserved.")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

// Setting ComVisible to false makes the types in this assembly not visible 
// to COM components.  If you need to access a type in this assembly from 
// COM, set the ComVisible attribute to true on that type.
[assembly: ComVisible(false)]

[assembly: CLSCompliant(true)]

// Version information for an assembly consists of the following four values:
//
//      Major Version
//      Minor Version 
//      Build Number
//      Revision
//
// You can specify all the values or you can default the Build and Revision Numbers 
// by using the '*' as shown below:
// [assembly: AssemblyVersion("1.0.*")]
// 1.8.3.0 2013.03.25. Signed and Strong Named.
// 1.9.0.0 2013.12.23. Go back to targetting framework 4.0.
// 1.9.1.0 2014.07.18. PersistentDictionary gets binary blobs; added Isam layer.
// 1.9.2.0 2014.09.11. Isam is placed in the Microsoft.Database namespace.
// 1.9.3.0 2015.08.11. Dependence added from Collections to Isam dll for configsets.
// 1.9.3.2 2015.09.02. Some bug fixes; go back to Framework 4.0
// 1.9.3.3 2016.03.01. Some bug and perf fixes.
// 1.9.4   2016.06.28. Some bug fixes.
// 1.9.4.1 2017.08.30. Adding JetGetIndexInfo that returns JET_INDEXCREATE.
[assembly: AssemblyVersion("1.9.4.1")]
[assembly: AssemblyFileVersion("1.9.4.1")]

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("EsentCollectionsTests, PublicKey=0024000004800000940000000602000000240000525341310004000001000100f570b92f384a3531e3093d62e905b48489fa7506a0c8ea3b7c59ab689be1da49f9a36b0038607ef95c0e9ba4cd75c0e983b2e4e59f19238971ebce82de56407f892319756ac283ba665257399b2ae2b333f1a3f72580c903965a5a9d23f6754f0acf64876a6c20e6236ef7775fbb0024be756a1086dec941a733c1ad053599c4")]
