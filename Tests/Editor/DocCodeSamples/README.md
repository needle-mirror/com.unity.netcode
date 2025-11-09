# Adding code snippets to the manual

Our documentation generally lives in /Documentation~/ in the package. However, we have moved manual code samples into this directory. The reason is that it allows us to actually compile the code samples, so that we ensure that the samples will mostly work. It does not guarantee the behaviour of those samples, as that would require tests to be written to run those samples. It is possible in theory, as they are actually compiled and can be referenced by code. This also means you will directly get intellisense when writing your code.

Note also the existance of `DisableAutoCreation.cs`, which disables systems and bootstraps from being created automatically and not influence our samples or user code when running tests. Note also that the assembly is guarded behind `UNITY_INCLUDE_TESTS` so they will not be compiled for users unless they are testing our package.

Markdown files have a 1-1 relationship with the code sample files, with file names being the same.

To create a sample to be used in the manual:

1. Find the corresponding `.cs` file or create one if it does not exist.
2. Try to use a private wrapper class to prevent leakage of the types into our test assemblies, and that the namespace is the same for all files. It will not always be possible to keep them private due to sourcegen and possibly other reasons.
3. Wrap the specific code sample in a region, e.g. `#region SampleRegion`.
4. In the markdown file, reference the sample with the filepath and region like so: `[!code-cs[blobs](../Tests/Editor/DocCodeSamples/client-server-worlds.cs#SampleRegion)]` (Ensure the end matches the region name)
