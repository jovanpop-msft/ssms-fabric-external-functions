# Contributor guidance

Read README.md and docs\development.md before changing the extension.

- Keep SQL parsing, metadata models and script generation independent of SSMS.
- Preserve explicit mutation authorization, identifier quoting, metadata checks
  and visible errors. Do not add automatic execution or retries.
- Never extract host credentials, patch SSMS or redistribute its assemblies.
- Interactive acceptance uses separately installed SSMS 22, never a Visual Studio
  experimental instance. Builds and tests must not install or launch SSMS or
  connect to a warehouse.
- Use scripts\Build.ps1 -SkipPackage for compilation and offline checks. Package
  only after reviewing the source and passing those checks.
- Keep test inputs fictional and logs, credentials and local output out of Git.
