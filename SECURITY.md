# Security Policy

## Supported Versions

| Version | Supported          |
|---------|--------------------|
| 2.1.x   | :white_check_mark: |
| < 2.1   | :x:                |

## Reporting a Vulnerability

If you discover a security vulnerability in Zexus, **please do not open a public issue.**

Instead, report it privately:

1. **GitHub Security Advisory** (preferred): Go to the [Security tab](https://github.com/QuanZ827/Zexus/security/advisories) and click "Report a vulnerability"
2. **Email**: Send details to **QuanZ827@users.noreply.github.com**

Please include:
- Description of the vulnerability
- Steps to reproduce
- Potential impact
- Suggested fix (if any)

## Response Timeline

- **Acknowledgment**: Within 48 hours
- **Initial assessment**: Within 1 week
- **Fix or mitigation**: Depends on severity, typically within 2 weeks for critical issues

## Security Considerations

### API Key Handling
- API keys are stored locally on the user's machine (in `appsettings.local.json` or `team_config.json`)
- Keys are never logged, transmitted to third parties, or included in telemetry
- The `.gitignore` excludes all configuration files containing secrets

### Data Flow
- Zexus sends Revit model metadata to the Anthropic API for AI processing
- No data is stored on external servers by Zexus itself
- Local usage logs contain no API keys or authentication tokens

### Code Execution
- The `ExecuteCode` tool compiles and runs C# code inside the Revit process
- All code execution requires explicit AI agent invocation (not arbitrary remote execution)
- Model-modifying operations require user confirmation before execution

## Best Practices for Users

1. **Never commit API keys** — Use `appsettings.local.json` (git-ignored) for your key
2. **Review AI-generated code** — Before confirming write operations, review what will change
3. **Save before automation** — Always save your Revit project before running AI-assisted batch operations
4. **Keep Zexus updated** — Use the latest release for security patches
