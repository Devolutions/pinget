//! HRESULT table for `pinget error`.
//!
//! The `APPINSTALLER_CLI_ERROR_*` entries (including the install-specific
//! `APPINSTALLER_CLI_ERROR_INSTALL_*` codes) mirror WinGet's
//! `src/AppInstallerSharedLib/Public/AppInstallerErrors.h` and the unlocalized
//! descriptions in `src/AppInstallerSharedLib/Errors.cpp`. A few common system
//! HRESULTs are included as well. Keep this table identical to
//! `dotnet/src/Devolutions.Pinget.Cli/ErrorCodes.cs`.

/// `(code, symbol, description)`, sorted by code.
pub(crate) const HRESULTS: &[(u32, &str, &str)] = &[
    (0x00000000, "S_OK", "The operation completed successfully."),
    (0x80004001, "E_NOTIMPL", "Not implemented"),
    (0x80004002, "E_NOINTERFACE", "No such interface supported"),
    (0x80004003, "E_POINTER", "Invalid pointer"),
    (0x80004004, "E_ABORT", "Operation aborted"),
    (0x80004005, "E_FAIL", "Unspecified error"),
    (
        0x80070002,
        "ERROR_FILE_NOT_FOUND",
        "The system cannot find the file specified.",
    ),
    (0x80070005, "E_ACCESSDENIED", "Access is denied."),
    (
        0x8007000E,
        "E_OUTOFMEMORY",
        "Not enough memory resources are available to complete this operation.",
    ),
    (0x80070057, "E_INVALIDARG", "The parameter is incorrect."),
    (0x8A150001, "APPINSTALLER_CLI_ERROR_INTERNAL_ERROR", "Internal Error"),
    (
        0x8A150002,
        "APPINSTALLER_CLI_ERROR_INVALID_CL_ARGUMENTS",
        "Invalid command line arguments",
    ),
    (
        0x8A150003,
        "APPINSTALLER_CLI_ERROR_COMMAND_FAILED",
        "Executing command failed",
    ),
    (
        0x8A150004,
        "APPINSTALLER_CLI_ERROR_MANIFEST_FAILED",
        "Opening manifest failed",
    ),
    (
        0x8A150005,
        "APPINSTALLER_CLI_ERROR_CTRL_SIGNAL_RECEIVED",
        "Cancellation signal received",
    ),
    (
        0x8A150006,
        "APPINSTALLER_CLI_ERROR_SHELLEXEC_INSTALL_FAILED",
        "Running ShellExecute failed",
    ),
    (
        0x8A150007,
        "APPINSTALLER_CLI_ERROR_UNSUPPORTED_MANIFESTVERSION",
        "Cannot process manifest. The manifest version is higher than supported. Please update the client.",
    ),
    (
        0x8A150008,
        "APPINSTALLER_CLI_ERROR_DOWNLOAD_FAILED",
        "Downloading installer failed",
    ),
    (
        0x8A150009,
        "APPINSTALLER_CLI_ERROR_CANNOT_WRITE_TO_UPLEVEL_INDEX",
        "Cannot write to index; it is a higher schema version",
    ),
    (
        0x8A15000A,
        "APPINSTALLER_CLI_ERROR_INDEX_INTEGRITY_COMPROMISED",
        "The index is corrupt",
    ),
    (
        0x8A15000B,
        "APPINSTALLER_CLI_ERROR_SOURCES_INVALID",
        "The configured source information is corrupt",
    ),
    (
        0x8A15000C,
        "APPINSTALLER_CLI_ERROR_SOURCE_NAME_ALREADY_EXISTS",
        "The source name is already configured",
    ),
    (
        0x8A15000D,
        "APPINSTALLER_CLI_ERROR_INVALID_SOURCE_TYPE",
        "The source type is invalid",
    ),
    (
        0x8A15000E,
        "APPINSTALLER_CLI_ERROR_PACKAGE_IS_BUNDLE",
        "The MSIX file is a bundle, not a package",
    ),
    (
        0x8A15000F,
        "APPINSTALLER_CLI_ERROR_SOURCE_DATA_MISSING",
        "Data required by the source is missing",
    ),
    (
        0x8A150010,
        "APPINSTALLER_CLI_ERROR_NO_APPLICABLE_INSTALLER",
        "None of the installers are applicable for the current system",
    ),
    (
        0x8A150011,
        "APPINSTALLER_CLI_ERROR_INSTALLER_HASH_MISMATCH",
        "The installer file's hash does not match the manifest",
    ),
    (
        0x8A150012,
        "APPINSTALLER_CLI_ERROR_SOURCE_NAME_DOES_NOT_EXIST",
        "The source name does not exist",
    ),
    (
        0x8A150013,
        "APPINSTALLER_CLI_ERROR_SOURCE_ARG_ALREADY_EXISTS",
        "The source location is already configured under another name",
    ),
    (
        0x8A150014,
        "APPINSTALLER_CLI_ERROR_NO_APPLICATIONS_FOUND",
        "No packages found",
    ),
    (
        0x8A150015,
        "APPINSTALLER_CLI_ERROR_NO_SOURCES_DEFINED",
        "No sources are configured",
    ),
    (
        0x8A150016,
        "APPINSTALLER_CLI_ERROR_MULTIPLE_APPLICATIONS_FOUND",
        "Multiple packages found matching the criteria",
    ),
    (
        0x8A150017,
        "APPINSTALLER_CLI_ERROR_NO_MANIFEST_FOUND",
        "No manifest found matching the criteria",
    ),
    (
        0x8A150018,
        "APPINSTALLER_CLI_ERROR_EXTENSION_PUBLIC_FAILED",
        "Failed to get Public folder from source package",
    ),
    (
        0x8A150019,
        "APPINSTALLER_CLI_ERROR_COMMAND_REQUIRES_ADMIN",
        "Command requires administrator privileges to run",
    ),
    (
        0x8A15001A,
        "APPINSTALLER_CLI_ERROR_SOURCE_NOT_SECURE",
        "The source location is not secure",
    ),
    (
        0x8A15001B,
        "APPINSTALLER_CLI_ERROR_MSSTORE_BLOCKED_BY_POLICY",
        "The Microsoft Store client is blocked by policy",
    ),
    (
        0x8A15001C,
        "APPINSTALLER_CLI_ERROR_MSSTORE_APP_BLOCKED_BY_POLICY",
        "The Microsoft Store app is blocked by policy",
    ),
    (
        0x8A15001D,
        "APPINSTALLER_CLI_ERROR_EXPERIMENTAL_FEATURE_DISABLED",
        "The feature is currently under development. It can be enabled using winget settings.",
    ),
    (
        0x8A15001E,
        "APPINSTALLER_CLI_ERROR_MSSTORE_INSTALL_FAILED",
        "Failed to install the Microsoft Store app",
    ),
    (
        0x8A15001F,
        "APPINSTALLER_CLI_ERROR_COMPLETE_INPUT_BAD",
        "Failed to perform auto complete",
    ),
    (
        0x8A150020,
        "APPINSTALLER_CLI_ERROR_YAML_INIT_FAILED",
        "Failed to initialize YAML parser",
    ),
    (
        0x8A150021,
        "APPINSTALLER_CLI_ERROR_YAML_INVALID_MAPPING_KEY",
        "Encountered an invalid YAML key",
    ),
    (
        0x8A150022,
        "APPINSTALLER_CLI_ERROR_YAML_DUPLICATE_MAPPING_KEY",
        "Encountered a duplicate YAML key",
    ),
    (
        0x8A150023,
        "APPINSTALLER_CLI_ERROR_YAML_INVALID_OPERATION",
        "Invalid YAML operation",
    ),
    (
        0x8A150024,
        "APPINSTALLER_CLI_ERROR_YAML_DOC_BUILD_FAILED",
        "Failed to build YAML doc",
    ),
    (
        0x8A150025,
        "APPINSTALLER_CLI_ERROR_YAML_INVALID_EMITTER_STATE",
        "Invalid YAML emitter state",
    ),
    (
        0x8A150026,
        "APPINSTALLER_CLI_ERROR_YAML_INVALID_DATA",
        "Invalid YAML data",
    ),
    (0x8A150027, "APPINSTALLER_CLI_ERROR_LIBYAML_ERROR", "LibYAML error"),
    (
        0x8A150028,
        "APPINSTALLER_CLI_ERROR_MANIFEST_VALIDATION_WARNING",
        "Manifest validation succeeded with warning",
    ),
    (
        0x8A150029,
        "APPINSTALLER_CLI_ERROR_MANIFEST_VALIDATION_FAILURE",
        "Manifest validation failed",
    ),
    (
        0x8A15002A,
        "APPINSTALLER_CLI_ERROR_INVALID_MANIFEST",
        "Manifest is invalid",
    ),
    (
        0x8A15002B,
        "APPINSTALLER_CLI_ERROR_UPDATE_NOT_APPLICABLE",
        "No applicable update found",
    ),
    (
        0x8A15002C,
        "APPINSTALLER_CLI_ERROR_UPDATE_ALL_HAS_FAILURE",
        "winget upgrade --all completed with failures",
    ),
    (
        0x8A15002D,
        "APPINSTALLER_CLI_ERROR_INSTALLER_SECURITY_CHECK_FAILED",
        "Installer failed security check",
    ),
    (
        0x8A15002E,
        "APPINSTALLER_CLI_ERROR_DOWNLOAD_SIZE_MISMATCH",
        "Download size does not match expected content length",
    ),
    (
        0x8A15002F,
        "APPINSTALLER_CLI_ERROR_NO_UNINSTALL_INFO_FOUND",
        "Uninstall command not found",
    ),
    (
        0x8A150030,
        "APPINSTALLER_CLI_ERROR_EXEC_UNINSTALL_COMMAND_FAILED",
        "Running uninstall command failed",
    ),
    (
        0x8A150031,
        "APPINSTALLER_CLI_ERROR_ICU_BREAK_ITERATOR_ERROR",
        "ICU break iterator error",
    ),
    (
        0x8A150032,
        "APPINSTALLER_CLI_ERROR_ICU_CASEMAP_ERROR",
        "ICU casemap error",
    ),
    (0x8A150033, "APPINSTALLER_CLI_ERROR_ICU_REGEX_ERROR", "ICU regex error"),
    (
        0x8A150034,
        "APPINSTALLER_CLI_ERROR_IMPORT_INSTALL_FAILED",
        "Failed to install one or more imported packages",
    ),
    (
        0x8A150035,
        "APPINSTALLER_CLI_ERROR_NOT_ALL_PACKAGES_FOUND",
        "Could not find one or more requested packages",
    ),
    (
        0x8A150036,
        "APPINSTALLER_CLI_ERROR_JSON_INVALID_FILE",
        "Json file is invalid",
    ),
    (
        0x8A150037,
        "APPINSTALLER_CLI_ERROR_SOURCE_NOT_REMOTE",
        "The source location is not remote",
    ),
    (
        0x8A150038,
        "APPINSTALLER_CLI_ERROR_UNSUPPORTED_RESTSOURCE",
        "The configured rest source is not supported",
    ),
    (
        0x8A150039,
        "APPINSTALLER_CLI_ERROR_RESTSOURCE_INVALID_DATA",
        "Invalid data returned by rest source",
    ),
    (
        0x8A15003A,
        "APPINSTALLER_CLI_ERROR_BLOCKED_BY_POLICY",
        "Operation is blocked by Group Policy",
    ),
    (
        0x8A15003B,
        "APPINSTALLER_CLI_ERROR_RESTAPI_INTERNAL_ERROR",
        "Rest API internal error",
    ),
    (
        0x8A15003C,
        "APPINSTALLER_CLI_ERROR_RESTSOURCE_INVALID_URL",
        "Invalid rest source url",
    ),
    (
        0x8A15003D,
        "APPINSTALLER_CLI_ERROR_RESTAPI_UNSUPPORTED_MIME_TYPE",
        "Unsupported MIME type returned by rest API",
    ),
    (
        0x8A15003E,
        "APPINSTALLER_CLI_ERROR_RESTSOURCE_INVALID_VERSION",
        "Invalid rest source contract version",
    ),
    (
        0x8A15003F,
        "APPINSTALLER_CLI_ERROR_SOURCE_DATA_INTEGRITY_FAILURE",
        "The source data is corrupted or tampered",
    ),
    (
        0x8A150040,
        "APPINSTALLER_CLI_ERROR_STREAM_READ_FAILURE",
        "Error reading from the stream",
    ),
    (
        0x8A150041,
        "APPINSTALLER_CLI_ERROR_PACKAGE_AGREEMENTS_NOT_ACCEPTED",
        "Package agreements were not agreed to",
    ),
    (
        0x8A150042,
        "APPINSTALLER_CLI_ERROR_PROMPT_INPUT_ERROR",
        "Error reading input in prompt",
    ),
    (
        0x8A150043,
        "APPINSTALLER_CLI_ERROR_UNSUPPORTED_SOURCE_REQUEST",
        "The search request is not supported by one or more sources",
    ),
    (
        0x8A150044,
        "APPINSTALLER_CLI_ERROR_RESTAPI_ENDPOINT_NOT_FOUND",
        "The rest API endpoint is not found.",
    ),
    (
        0x8A150045,
        "APPINSTALLER_CLI_ERROR_SOURCE_OPEN_FAILED",
        "Failed to open the source.",
    ),
    (
        0x8A150046,
        "APPINSTALLER_CLI_ERROR_SOURCE_AGREEMENTS_NOT_ACCEPTED",
        "Source agreements were not agreed to",
    ),
    (
        0x8A150047,
        "APPINSTALLER_CLI_ERROR_CUSTOMHEADER_EXCEEDS_MAXLENGTH",
        "Header size exceeds the allowable limit of 1024 characters. Please reduce the size and try again.",
    ),
    (
        0x8A150048,
        "APPINSTALLER_CLI_ERROR_MISSING_RESOURCE_FILE",
        "Missing resource file",
    ),
    (
        0x8A150049,
        "APPINSTALLER_CLI_ERROR_MSI_INSTALL_FAILED",
        "Running MSI install failed",
    ),
    (
        0x8A15004A,
        "APPINSTALLER_CLI_ERROR_INVALID_MSIEXEC_ARGUMENT",
        "Arguments for msiexec are invalid",
    ),
    (
        0x8A15004B,
        "APPINSTALLER_CLI_ERROR_FAILED_TO_OPEN_ALL_SOURCES",
        "Failed to open one or more sources",
    ),
    (
        0x8A15004C,
        "APPINSTALLER_CLI_ERROR_DEPENDENCIES_VALIDATION_FAILED",
        "Failed to validate dependencies",
    ),
    (
        0x8A15004D,
        "APPINSTALLER_CLI_ERROR_MISSING_PACKAGE",
        "One or more package is missing",
    ),
    (
        0x8A15004E,
        "APPINSTALLER_CLI_ERROR_INVALID_TABLE_COLUMN",
        "Invalid table column",
    ),
    (
        0x8A15004F,
        "APPINSTALLER_CLI_ERROR_UPGRADE_VERSION_NOT_NEWER",
        "The upgrade version is not newer than the installed version",
    ),
    (
        0x8A150050,
        "APPINSTALLER_CLI_ERROR_UPGRADE_VERSION_UNKNOWN",
        "Upgrade version is unknown and override is not specified",
    ),
    (
        0x8A150051,
        "APPINSTALLER_CLI_ERROR_ICU_CONVERSION_ERROR",
        "ICU conversion error",
    ),
    (
        0x8A150052,
        "APPINSTALLER_CLI_ERROR_PORTABLE_INSTALL_FAILED",
        "Failed to install portable package",
    ),
    (
        0x8A150053,
        "APPINSTALLER_CLI_ERROR_PORTABLE_REPARSE_POINT_NOT_SUPPORTED",
        "Volume does not support reparse points",
    ),
    (
        0x8A150054,
        "APPINSTALLER_CLI_ERROR_PORTABLE_PACKAGE_ALREADY_EXISTS",
        "Portable package from a different source already exists.",
    ),
    (
        0x8A150055,
        "APPINSTALLER_CLI_ERROR_PORTABLE_SYMLINK_PATH_IS_DIRECTORY",
        "Unable to create symlink, path points to a directory.",
    ),
    (
        0x8A150056,
        "APPINSTALLER_CLI_ERROR_INSTALLER_PROHIBITS_ELEVATION",
        "The installer cannot be run from an administrator context.",
    ),
    (
        0x8A150057,
        "APPINSTALLER_CLI_ERROR_PORTABLE_UNINSTALL_FAILED",
        "Failed to uninstall portable package",
    ),
    (
        0x8A150058,
        "APPINSTALLER_CLI_ERROR_ARP_VERSION_VALIDATION_FAILED",
        "Failed to validate DisplayVersion values against index.",
    ),
    (
        0x8A150059,
        "APPINSTALLER_CLI_ERROR_UNSUPPORTED_ARGUMENT",
        "One or more arguments are not supported.",
    ),
    (
        0x8A15005A,
        "APPINSTALLER_CLI_ERROR_BIND_WITH_EMBEDDED_NULL",
        "Embedded null characters are disallowed for SQLite",
    ),
    (
        0x8A15005B,
        "APPINSTALLER_CLI_ERROR_NESTEDINSTALLER_NOT_FOUND",
        "Failed to find the nested installer in the archive.",
    ),
    (
        0x8A15005C,
        "APPINSTALLER_CLI_ERROR_EXTRACT_ARCHIVE_FAILED",
        "Failed to extract archive.",
    ),
    (
        0x8A15005D,
        "APPINSTALLER_CLI_ERROR_NESTEDINSTALLER_INVALID_PATH",
        "Invalid relative file path to nested installer provided.",
    ),
    (
        0x8A15005E,
        "APPINSTALLER_CLI_ERROR_PINNED_CERTIFICATE_MISMATCH",
        "The server certificate did not match any of the expected values.",
    ),
    (
        0x8A15005F,
        "APPINSTALLER_CLI_ERROR_INSTALL_LOCATION_REQUIRED",
        "Install location must be provided.",
    ),
    (
        0x8A150060,
        "APPINSTALLER_CLI_ERROR_ARCHIVE_SCAN_FAILED",
        "Archive malware scan failed.",
    ),
    (
        0x8A150061,
        "APPINSTALLER_CLI_ERROR_PACKAGE_ALREADY_INSTALLED",
        "Found at least one version of the package installed.",
    ),
    (
        0x8A150062,
        "APPINSTALLER_CLI_ERROR_PIN_ALREADY_EXISTS",
        "A pin already exists for the package.",
    ),
    (
        0x8A150063,
        "APPINSTALLER_CLI_ERROR_PIN_DOES_NOT_EXIST",
        "There is no pin for the package.",
    ),
    (
        0x8A150064,
        "APPINSTALLER_CLI_ERROR_CANNOT_OPEN_PINNING_INDEX",
        "Unable to open the pin database.",
    ),
    (
        0x8A150065,
        "APPINSTALLER_CLI_ERROR_MULTIPLE_INSTALL_FAILED",
        "One or more applications failed to install",
    ),
    (
        0x8A150066,
        "APPINSTALLER_CLI_ERROR_MULTIPLE_UNINSTALL_FAILED",
        "One or more applications failed to uninstall",
    ),
    (
        0x8A150067,
        "APPINSTALLER_CLI_ERROR_NOT_ALL_QUERIES_FOUND_SINGLE",
        "One or more queries did not return exactly one match",
    ),
    (
        0x8A150068,
        "APPINSTALLER_CLI_ERROR_PACKAGE_IS_PINNED",
        "The package has a pin that prevents upgrade.",
    ),
    (
        0x8A150069,
        "APPINSTALLER_CLI_ERROR_PACKAGE_IS_STUB",
        "The package currently installed is the stub package",
    ),
    (
        0x8A15006A,
        "APPINSTALLER_CLI_ERROR_APPTERMINATION_RECEIVED",
        "Application shutdown signal received",
    ),
    (
        0x8A15006B,
        "APPINSTALLER_CLI_ERROR_DOWNLOAD_DEPENDENCIES",
        "Failed to download package dependencies.",
    ),
    (
        0x8A15006C,
        "APPINSTALLER_CLI_ERROR_DOWNLOAD_COMMAND_PROHIBITED",
        "Failed to download package. Download for offline installation is prohibited.",
    ),
    (
        0x8A15006D,
        "APPINSTALLER_CLI_ERROR_SERVICE_UNAVAILABLE",
        "A required service is busy or unavailable. Try again later.",
    ),
    (
        0x8A15006E,
        "APPINSTALLER_CLI_ERROR_RESUME_ID_NOT_FOUND",
        "The guid provided does not correspond to a valid resume state.",
    ),
    (
        0x8A15006F,
        "APPINSTALLER_CLI_ERROR_CLIENT_VERSION_MISMATCH",
        "The current client version did not match the client version of the saved state.",
    ),
    (
        0x8A150070,
        "APPINSTALLER_CLI_ERROR_INVALID_RESUME_STATE",
        "The resume state data is invalid.",
    ),
    (
        0x8A150071,
        "APPINSTALLER_CLI_ERROR_CANNOT_OPEN_CHECKPOINT_INDEX",
        "Unable to open the checkpoint database.",
    ),
    (
        0x8A150072,
        "APPINSTALLER_CLI_ERROR_RESUME_LIMIT_EXCEEDED",
        "Exceeded max resume limit.",
    ),
    (
        0x8A150073,
        "APPINSTALLER_CLI_ERROR_INVALID_AUTHENTICATION_INFO",
        "Invalid authentication info.",
    ),
    (
        0x8A150074,
        "APPINSTALLER_CLI_ERROR_AUTHENTICATION_TYPE_NOT_SUPPORTED",
        "Authentication method not supported.",
    ),
    (
        0x8A150075,
        "APPINSTALLER_CLI_ERROR_AUTHENTICATION_FAILED",
        "Authentication failed.",
    ),
    (
        0x8A150076,
        "APPINSTALLER_CLI_ERROR_AUTHENTICATION_INTERACTIVE_REQUIRED",
        "Authentication failed. Interactive authentication required.",
    ),
    (
        0x8A150077,
        "APPINSTALLER_CLI_ERROR_AUTHENTICATION_CANCELLED_BY_USER",
        "Authentication failed. User cancelled.",
    ),
    (
        0x8A150078,
        "APPINSTALLER_CLI_ERROR_AUTHENTICATION_INCORRECT_ACCOUNT",
        "Authentication failed. Authenticated account is not the desired account.",
    ),
    (
        0x8A150079,
        "APPINSTALLER_CLI_ERROR_NO_REPAIR_INFO_FOUND",
        "Repair command not found.",
    ),
    (
        0x8A15007A,
        "APPINSTALLER_CLI_ERROR_REPAIR_NOT_APPLICABLE",
        "Repair operation is not applicable.",
    ),
    (
        0x8A15007B,
        "APPINSTALLER_CLI_ERROR_EXEC_REPAIR_FAILED",
        "Repair operation failed.",
    ),
    (
        0x8A15007C,
        "APPINSTALLER_CLI_ERROR_REPAIR_NOT_SUPPORTED",
        "The installer technology in use doesn't support repair.",
    ),
    (
        0x8A15007D,
        "APPINSTALLER_CLI_ERROR_ADMIN_CONTEXT_ACTION_PROHIBITED",
        "The requested operation is not permitted from an administrator context on packages installed within the user scope.",
    ),
    (
        0x8A15007E,
        "APPINSTALLER_CLI_ERROR_SQLITE_CONNECTION_TERMINATED",
        "The SQLite connection was terminated to prevent corruption.",
    ),
    (
        0x8A15007F,
        "APPINSTALLER_CLI_ERROR_DISPLAYCATALOG_API_FAILED",
        "Failed to get Microsoft Store package catalog.",
    ),
    (
        0x8A150080,
        "APPINSTALLER_CLI_ERROR_NO_APPLICABLE_DISPLAYCATALOG_PACKAGE",
        "No applicable Microsoft Store package found from Microsoft Store package catalog.",
    ),
    (
        0x8A150081,
        "APPINSTALLER_CLI_ERROR_SFSCLIENT_API_FAILED",
        "Failed to get Microsoft Store package download information.",
    ),
    (
        0x8A150082,
        "APPINSTALLER_CLI_ERROR_NO_APPLICABLE_SFSCLIENT_PACKAGE",
        "No applicable Microsoft Store package download information found.",
    ),
    (
        0x8A150083,
        "APPINSTALLER_CLI_ERROR_LICENSING_API_FAILED",
        "Failed to retrieve Microsoft Store package license.",
    ),
    (
        0x8A150084,
        "APPINSTALLER_CLI_ERROR_SFSCLIENT_PACKAGE_NOT_SUPPORTED",
        "The Microsoft Store package does not support download.",
    ),
    (
        0x8A150085,
        "APPINSTALLER_CLI_ERROR_LICENSING_API_FAILED_FORBIDDEN",
        "Failed to retrieve Microsoft Store package license. The Microsoft Entra Id account does not have the required privilege.",
    ),
    (
        0x8A150086,
        "APPINSTALLER_CLI_ERROR_INSTALLER_ZERO_BYTE_FILE",
        "Downloaded zero byte installer; ensure that your network connection is working properly.",
    ),
    (
        0x8A150087,
        "APPINSTALLER_CLI_ERROR_FONT_INSTALL_FAILED",
        "Failed installing one or more fonts.",
    ),
    (
        0x8A150088,
        "APPINSTALLER_CLI_ERROR_FONT_FILE_NOT_SUPPORTED",
        "Font file is not supported and cannot be installed.",
    ),
    (
        0x8A150089,
        "APPINSTALLER_CLI_ERROR_FONT_ALREADY_INSTALLED",
        "Font package is already installed.",
    ),
    (
        0x8A15008A,
        "APPINSTALLER_CLI_ERROR_FONT_FILE_NOT_FOUND",
        "Font file not found.",
    ),
    (
        0x8A15008B,
        "APPINSTALLER_CLI_ERROR_FONT_UNINSTALL_FAILED",
        "Font uninstall failed. The font may not be in a good state. Try uninstalling after a restart.",
    ),
    (
        0x8A15008C,
        "APPINSTALLER_CLI_ERROR_FONT_VALIDATION_FAILED",
        "Font validation failed.",
    ),
    (
        0x8A15008D,
        "APPINSTALLER_CLI_ERROR_FONT_ROLLBACK_FAILED",
        "Font rollback failed. The font may not be in a good state. Try uninstalling after a restart.",
    ),
    (
        0x8A15008E,
        "APPINSTALLER_CLI_ERROR_UPDATE_INSTALL_TECHNOLOGY_MISMATCH",
        "An upgrade is available but uses a different install technology than the current installation",
    ),
    (
        0x8A150101,
        "APPINSTALLER_CLI_ERROR_INSTALL_PACKAGE_IN_USE",
        "Application is currently running. Exit the application then try again.",
    ),
    (
        0x8A150102,
        "APPINSTALLER_CLI_ERROR_INSTALL_INSTALL_IN_PROGRESS",
        "Another installation is already in progress. Try again later.",
    ),
    (
        0x8A150103,
        "APPINSTALLER_CLI_ERROR_INSTALL_FILE_IN_USE",
        "One or more file is being used. Exit the application then try again.",
    ),
    (
        0x8A150104,
        "APPINSTALLER_CLI_ERROR_INSTALL_MISSING_DEPENDENCY",
        "This package has a dependency missing from your system.",
    ),
    (
        0x8A150105,
        "APPINSTALLER_CLI_ERROR_INSTALL_DISK_FULL",
        "There's no more space on your PC. Make space, then try again.",
    ),
    (
        0x8A150106,
        "APPINSTALLER_CLI_ERROR_INSTALL_INSUFFICIENT_MEMORY",
        "There's not enough memory available to install. Close other applications then try again.",
    ),
    (
        0x8A150107,
        "APPINSTALLER_CLI_ERROR_INSTALL_NO_NETWORK",
        "This application requires internet connectivity. Connect to a network then try again.",
    ),
    (
        0x8A150108,
        "APPINSTALLER_CLI_ERROR_INSTALL_CONTACT_SUPPORT",
        "This application encountered an error during installation. Contact support.",
    ),
    (
        0x8A150109,
        "APPINSTALLER_CLI_ERROR_INSTALL_REBOOT_REQUIRED_TO_FINISH",
        "Restart your PC to finish installation.",
    ),
    (
        0x8A15010A,
        "APPINSTALLER_CLI_ERROR_INSTALL_REBOOT_REQUIRED_FOR_INSTALL",
        "Installation failed. Restart your PC then try again.",
    ),
    (
        0x8A15010B,
        "APPINSTALLER_CLI_ERROR_INSTALL_REBOOT_INITIATED",
        "Your PC will restart to finish installation.",
    ),
    (
        0x8A15010C,
        "APPINSTALLER_CLI_ERROR_INSTALL_CANCELLED_BY_USER",
        "You cancelled the installation.",
    ),
    (
        0x8A15010D,
        "APPINSTALLER_CLI_ERROR_INSTALL_ALREADY_INSTALLED",
        "Another version of this application is already installed.",
    ),
    (
        0x8A15010E,
        "APPINSTALLER_CLI_ERROR_INSTALL_DOWNGRADE",
        "A higher version of this application is already installed.",
    ),
    (
        0x8A15010F,
        "APPINSTALLER_CLI_ERROR_INSTALL_BLOCKED_BY_POLICY",
        "Organization policies are preventing installation. Contact your admin.",
    ),
    (
        0x8A150110,
        "APPINSTALLER_CLI_ERROR_INSTALL_DEPENDENCIES",
        "Failed to install package dependencies.",
    ),
    (
        0x8A150111,
        "APPINSTALLER_CLI_ERROR_INSTALL_PACKAGE_IN_USE_BY_APPLICATION",
        "Application is currently in use by another application.",
    ),
    (
        0x8A150112,
        "APPINSTALLER_CLI_ERROR_INSTALL_INVALID_PARAMETER",
        "Invalid parameter.",
    ),
    (
        0x8A150113,
        "APPINSTALLER_CLI_ERROR_INSTALL_SYSTEM_NOT_SUPPORTED",
        "Package not supported by the system.",
    ),
    (
        0x8A150114,
        "APPINSTALLER_CLI_ERROR_INSTALL_UPGRADE_NOT_SUPPORTED",
        "The installer does not support upgrading an existing package.",
    ),
    (
        0x8A150115,
        "APPINSTALLER_CLI_ERROR_INSTALL_CUSTOM_ERROR",
        "Installation failed with a custom installer error.",
    ),
];

/// Looks up an HRESULT in [`HRESULTS`].
pub(crate) fn lookup_hresult(code: u32) -> Option<(&'static str, &'static str)> {
    HRESULTS
        .binary_search_by_key(&code, |&(value, _, _)| value)
        .ok()
        .map(|index| (HRESULTS[index].1, HRESULTS[index].2))
}

/// Parses `pinget error` input: `0x`-prefixed values are hexadecimal; anything else is a
/// decimal number in `i32::MIN..=u32::MAX`, with negative values reinterpreted as the
/// unsigned HRESULT bit pattern (so `-2146233088` and `0x80131600` are the same code).
pub(crate) fn parse_error_code(input: &str) -> Option<u32> {
    let input = input.trim();
    if let Some(hex) = input.strip_prefix("0x").or_else(|| input.strip_prefix("0X")) {
        if hex.is_empty() || !hex.bytes().all(|byte| byte.is_ascii_hexdigit()) {
            return None;
        }
        return u32::from_str_radix(hex, 16).ok();
    }

    let value = input.parse::<i64>().ok()?;
    if value < 0 {
        i32::try_from(value).ok().map(i32::cast_unsigned)
    } else {
        u32::try_from(value).ok()
    }
}
