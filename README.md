# ESENT Managed Interop
ManagedEsent provides managed access to esent.dll, the embeddable database engine native to Windows.

The **[Microsoft.Isam.Esent.Interop](Documentation/ManagedEsentDocumentation.md)** namespace in EsentInterop.dll provides managed access to the basic ESENT API. Use this for applications that want access to the full ESENT feature set.

The **[PersistentDictionary](Documentation/PersistentDictionaryDocumentation.md)** class in EsentCollections.dll provides a persistent, generic dictionary for .NET, with LINQ support. A PersistentDictionary is backed by an ESENT database and can be used to replace a standard Dictionary, HashTable, or SortedList. Use it when you want extremely simple, reliable and fast data persistence.

**esedb** provides both dbm and shelve modules built on top of ESENT IronPython users.

# Sandcastle Help File Builder Customizations
This fork is used by the Sandcastle Help File Builder and contains a few changes specifically for use with it when
used to cache reflection and comments file data.

* Serialization is enabled by default.
* Added a static property to `PersistentDictionaryFile` to indicate reference type serialization status.
* Updated `ColumnConverter` to allow serialization of reference types.
* Added the option to `PersistentDictionary` to disable column compression using a new constructor.
* Increased `MaxVerPages` in `PersistentDictionary` to 4096 as fast loading of large amounts of data in parallel fills the version store.
* Added the option to utilize a local cache for read-only operations to speed up access to the most recently used and frequently used values.
* Strong named the assemblies.
