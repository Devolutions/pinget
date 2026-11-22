#requires -Version 7.4
[CmdletBinding()]
param([string]$Artifacts = $PSScriptRoot)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'mszip-interop.ps1')

$script:SmallCases = @(
    @{ name = 'empty'; size = 0; pattern = 'empty' }
    @{ name = 'single_byte'; size = 1; pattern = 'single' }
    @{ name = 'block_32k_after'; size = 32769; pattern = 'random' }
)
$script:EmptyFrame = [Convert]::FromHexString('0a51e5c01800000200000000000000000080000000000000')
$script:SingleFrame = [Convert]::FromHexString('0a51e5c018001f020100000000000000010000000000000005000000434b5b0e00')
function Set-HeaderChecksum([byte[]]$Data) {
    [byte[]]$crcData = @($Data[0..5]) + @($Data[7..23])
    $Data[6] = [byte]([Pinget.Interop.Bytes]::Crc32($crcData) -band 255)
}
Set-HeaderChecksum $script:EmptyFrame
function Assert-Equal($Expected, $Actual) {
    if ($Expected -is [byte[]]) {
        Assert-Interop ($Actual -is [byte[]] -and [Pinget.Interop.Bytes]::Equal($Expected, $Actual)) 'byte arrays differ'
    } else {
        Assert-Interop (($Expected | ConvertTo-Json -Depth 100 -Compress) -ceq ($Actual | ConvertTo-Json -Depth 100 -Compress)) "expected $($Expected | ConvertTo-Json -Compress), got $($Actual | ConvertTo-Json -Compress)"
    }
}
function Assert-Throws([scriptblock]$Action, [string]$Pattern = '.', [type]$Type = [Pinget.Interop.VerificationError]) {
    try { $null = & $Action } catch {
        $value = Get-InteropException $_
        Assert-Interop ($Type.IsInstanceOfType($value)) "expected $Type, got $($value.GetType()): $value"
        Assert-Interop ($value.Message -match $Pattern) "exception did not match '$Pattern': $($value.Message)"
        return $value
    }
    throw [Pinget.Interop.VerificationError]::new("expected exception matching '$Pattern'")
}
function Replace-Json([string]$Path, $Value) {
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 100), [Text.UTF8Encoding]::new($false))
}
function New-TestFixture {
    $script:Root = Join-Path $script:TestDirectory 'fixtures'
    Initialize-Fixture $script:Root 'run-42' $script:SmallCases
    foreach ($producer in $script:Producers) {
        $directory = Join-Path $script:Root $producer
        $null = [IO.Directory]::CreateDirectory($directory)
        foreach ($case in $script:SmallCases) {
            # Identity artifacts are protocol-test fakes, never production fixtures.
            [IO.File]::WriteAllBytes((Join-Path $directory "$($case.name).mszip"), (Get-Payload $case))
        }
        New-ProducerManifest $script:Root 'run-42' $producer $script:SmallCases
    }
    Write-InteropJson (Join-Path $script:Root 'complete.json') (Get-CompletionRecord $script:Root 'run-42')
}
function Invoke-TestValidation([string]$RunId = 'run-42', [scriptblock]$Decoder = { param($data, $size) return ,$data }) {
    Test-FixtureTree $script:Root $RunId $Decoder -Cases $script:SmallCases -FrameCheck { param($data, $size) } -VerboseOutput $false
}
function Reseal-Fixture { Replace-Json (Join-Path $script:Root 'complete.json') (Get-CompletionRecord $script:Root 'run-42') }
function Invoke-WithMocks([hashtable]$Mocks, [scriptblock]$Action) {
    $original = @{}
    try {
        foreach ($name in $Mocks.Keys) {
            $original[$name] = (Get-Item "Function:$name").ScriptBlock
            Set-Item "Function:script:$name" $Mocks[$name]
        }
        & $Action
    } finally {
        foreach ($name in $original.Keys) { Set-Item "Function:script:$name" $original[$name] }
    }
}
function New-FakeNative([scriptblock]$Decompress, [scriptblock]$Compress = { param($data) return ,$script:SingleFrame }) {
    $value = [pscustomobject]@{}
    $value | Add-Member ScriptMethod Decompress $Decompress
    $value | Add-Member ScriptMethod CompressReference $Compress
    return $value
}
function New-Api {
    $script:Api = [Pinget.Interop.NativeApiCallbacks]::new()
    $script:Api.Handle = [IntPtr]11
    $script:Trace = [Collections.Generic.List[object]]::new()
    $script:Api.OnCreate = {
        param($compressor, $algorithm, $options)
        $script:Trace.Add(@('create', [int]$algorithm, ($options -eq [IntPtr]::Zero)))
    }
    $script:Api.OnClose = {
        param($compressor, $handle)
        $script:Trace.Add(@('close', $handle.ToInt64()))
    }
    return [Pinget.Interop.WindowsNativeMsZip]::new($script:Api)
}
function Get-CompressedSize([byte[]]$Data) {
    $destination = [IO.MemoryStream]::new()
    try {
        $compressor = [IO.Compression.ZLibStream]::new($destination, [IO.Compression.CompressionLevel]::Optimal, $true)
        try { $compressor.Write($Data) } finally { $compressor.Dispose() }
        return $destination.Length
    } finally { $destination.Dispose() }
}

$tests = [ordered]@{
    test_exact_set_compares_each_producer_and_case = {
        New-TestFixture
        $script:Trace = [Collections.Generic.List[object]]::new()
        Invoke-TestValidation -Decoder {
            param($data, $size)
            $script:Trace.Add(@($data.Length, $size))
            return ,$data
        }
        Assert-Equal @(@(0, 0), @(0, 0), @(1, 1), @(1, 1), @(32769, 32769), @(32769, 32769)) $script:Trace.ToArray()
    }
    test_stale_external_run_id_is_rejected = {
        New-TestFixture
        $null = Assert-Throws { Invoke-TestValidation 'run-previous' } 'stale or corrupt completion marker'
    }
    test_stale_producer_manifest_is_rejected_even_with_new_seal = {
        New-TestFixture
        $path = Join-Path $script:Root 'rust/manifest.json'
        $manifest = Read-InteropJson $path
        $manifest.run_id = 'run-previous'
        Replace-Json $path $manifest
        Reseal-Fixture
        $null = Assert-Throws { Invoke-TestValidation } 'stale rust manifest'
    }
    test_missing_completion_marker_is_rejected = {
        New-TestFixture
        [IO.File]::Delete((Join-Path $script:Root 'complete.json'))
        $null = Assert-Throws { Invoke-TestValidation } 'missing=.*complete.json'
    }
    test_missing_producer_fixture_is_rejected = {
        New-TestFixture
        [IO.File]::Delete((Join-Path $script:Root 'dotnet/single_byte.mszip'))
        $null = Assert-Throws { Invoke-TestValidation } 'missing=.*single_byte.mszip'
    }
    test_missing_entire_producer_set_is_rejected = {
        New-TestFixture
        [IO.Directory]::Move((Join-Path $script:Root 'rust'), (Join-Path $script:TestDirectory 'removed-rust'))
        $null = Assert-Throws { Invoke-TestValidation } 'missing=.*rust'
    }
    test_extra_case_or_file_is_rejected = {
        New-TestFixture
        [IO.File]::WriteAllBytes((Join-Path $script:Root 'rust/old_case.mszip'), [Text.Encoding]::ASCII.GetBytes('old'))
        $null = Assert-Throws { Invoke-TestValidation } 'extra=.*old_case.mszip'
    }
    test_extra_root_directory_is_rejected = {
        New-TestFixture
        $null = [IO.Directory]::CreateDirectory((Join-Path $script:Root 'old_producer'))
        $null = Assert-Throws { Invoke-TestValidation } 'extra=.*old_producer'
    }
    test_corrupted_encoded_bytes_fail_manifest_integrity = {
        New-TestFixture
        [IO.File]::WriteAllBytes((Join-Path $script:Root 'rust/single_byte.mszip'), [byte[]]@(0))
        $null = Assert-Throws { Invoke-TestValidation } 'corrupt rust fixture for single_byte'
    }
    test_wrong_decoded_bytes_fail_even_if_hash_and_seal_are_recomputed = {
        New-TestFixture
        [IO.File]::WriteAllBytes((Join-Path $script:Root 'rust/single_byte.mszip'), [byte[]]@(0))
        [IO.File]::Delete((Join-Path $script:Root 'rust/manifest.json'))
        New-ProducerManifest $script:Root 'run-42' 'rust' $script:SmallCases
        Reseal-Fixture
        $null = Assert-Throws { Invoke-TestValidation } 'decoded bytes differ for rust/single_byte'
    }
    test_decoder_failures_are_reported_after_all_producers_and_cases = {
        New-TestFixture
        $script:Trace = [Collections.Generic.List[object]]::new()
        $errorValue = Assert-Throws {
            Invoke-TestValidation -Decoder {
                param($data, $size)
                $script:Trace.Add($size)
                if ($size -eq 0) { throw [Pinget.Interop.NativeDecodeError]::new('empty stream rejected') }
                return ,$data
            }
        } '2 fixture\(s\) failed'
        Assert-Interop ($errorValue.Message.Contains('rust/empty')) 'missing rust failure'
        Assert-Interop ($errorValue.Message.Contains('dotnet/empty')) 'missing dotnet failure'
        Assert-Equal @(0, 0, 1, 1, 32769, 32769) $script:Trace.ToArray()
    }
    test_corrupted_original_fails_even_if_manifest_hash_is_recomputed = {
        New-TestFixture
        [IO.File]::WriteAllBytes((Join-Path $script:Root 'inputs/single_byte.bin'), [byte[]]@(0))
        $path = Join-Path $script:Root 'run.json'
        $manifest = Read-InteropJson $path
        $manifest.cases[1].sha256 = Get-Digest ([byte[]]@(0))
        Replace-Json $path $manifest
        Reseal-Fixture
        $null = Assert-Throws { Invoke-TestValidation } 'original payload differs.*single_byte'
    }
    test_reordered_manifest_cases_are_rejected = {
        New-TestFixture
        $path = Join-Path $script:Root 'dotnet/manifest.json'
        $manifest = Read-InteropJson $path
        [array]::Reverse($manifest.cases)
        Replace-Json $path $manifest
        Reseal-Fixture
        $null = Assert-Throws { Invoke-TestValidation } 'corrupt dotnet fixture for empty'
    }
    test_extra_manifest_case_is_rejected_even_with_new_seal = {
        New-TestFixture
        $path = Join-Path $script:Root 'rust/manifest.json'
        $manifest = Read-InteropJson $path
        $manifest.cases += $manifest.cases[0]
        Replace-Json $path $manifest
        Reseal-Fixture
        $null = Assert-Throws { Invoke-TestValidation } 'missing or extra rust cases'
    }
    test_duplicate_json_keys_are_rejected = {
        New-TestFixture
        [IO.File]::WriteAllText((Join-Path $script:Root 'run.json'), '{"protocol":"one","protocol":"two"}')
        Reseal-Fixture
        $null = Assert-Throws { Invoke-TestValidation } 'duplicate JSON key'
    }
    test_existing_output_directory_is_never_overwritten = {
        New-TestFixture
        Invoke-WithMocks @{ 'Get-InteropPlatform' = { 'linux' } } {
            $null = Assert-Throws { Invoke-Produce $script:Root (Join-Path $script:TestDirectory 'new-reports') 'run-43' } 'output already exists'
        }
        Invoke-TestValidation
    }
    test_failed_producer_never_writes_completion_marker = {
        $script:FailedRoot = Join-Path $script:TestDirectory 'failed-run'
        $script:FailedReports = Join-Path $script:TestDirectory 'failed-reports'
        $script:InitializeOriginal = (Get-Item Function:Initialize-Fixture).ScriptBlock
        Invoke-WithMocks @{
            'Get-InteropPlatform' = { 'linux' }
            'Initialize-Fixture' = { param($path, $runId) & $script:InitializeOriginal $path $runId $script:SmallCases }
            'Invoke-InteropCommand' = { throw [Pinget.Interop.VerificationError]::new('test runner failed') }
        } {
            $null = Assert-Throws { Invoke-Produce $script:FailedRoot $script:FailedReports 'run-43' } 'test runner failed'
        }
        Assert-Interop (-not (Test-Path (Join-Path $script:FailedRoot 'complete.json'))) 'failed producer sealed fixtures'
        Assert-Equal (Get-Payload $script:SmallCases[1]) (Read-Fixture (Join-Path $script:FailedRoot 'inputs/single_byte.bin'))
    }
    test_fixture_producer_accepts_macos_and_rejects_windows = {
        $script:PlatformRoot = Join-Path $script:TestDirectory 'platform-run'
        $script:PlatformReports = Join-Path $script:TestDirectory 'platform-reports'
        Invoke-WithMocks @{ 'Get-InteropPlatform' = { 'windows' } } {
            $null = Assert-Throws { Invoke-Produce $script:PlatformRoot $script:PlatformReports 'run-43' } 'require Linux or macOS'
        }
        Assert-Interop (-not (Test-Path $script:PlatformRoot)) 'Windows created fixtures'
        Assert-Interop (-not (Test-Path $script:PlatformReports)) 'Windows created reports'
        Invoke-WithMocks @{
            'Get-InteropPlatform' = { 'macos' }
            'Initialize-Fixture' = { throw [Pinget.Interop.VerificationError]::new('entered fixture initialization') }
        } {
            $null = Assert-Throws { Invoke-Produce $script:PlatformRoot $script:PlatformReports 'run-43' } 'entered fixture initialization'
        }
    }
    test_inventory_covers_exact_boundaries_and_both_multiblock_patterns = {
        $expected = @(
            ,@('single_byte', 1, 'single')
            ,@('block_32k_before', 32767, 'random')
            ,@('block_32k_at', 32768, 'random')
            ,@('block_32k_after', 32769, 'random')
            ,@('block_64k_before', 65535, 'random')
            ,@('block_64k_at', 65536, 'random')
            ,@('block_64k_after', 65537, 'random')
            ,@('multiblock_compressible', 98441, 'repeated')
            ,@('multiblock_incompressible', 98441, 'random')
            ,@('limit_64m_before', 67108863, 'repeated')
            ,@('limit_64m_at', 67108864, 'repeated')
        )
        $actual = @(foreach ($case in $script:Cases) { ,@($case.name, $case.size, $case.pattern) })
        Assert-Equal $expected $actual
        Assert-Equal ([byte[]]@(167)) (Get-Payload $script:Cases[0])
        $repeated = Get-Payload $script:Cases[7]
        $random = Get-Payload $script:Cases[8]
        Assert-Interop (-not [Pinget.Interop.Bytes]::Equal([byte[]]$repeated[0..63], [byte[]]$repeated[32768..32831])) 'repeated block reused exact sequence'
        Assert-Interop (-not [Pinget.Interop.Bytes]::Equal([byte[]]$random[0..63], [byte[]]$random[32768..32831])) 'random block reused exact sequence'
        Assert-Equal $random (Get-Payload $script:Cases[8])
        Assert-Interop ((Get-CompressedSize $repeated) -lt [Math]::Floor($repeated.Length / 10)) 'repeated payload not compressible'
        Assert-Interop ((Get-CompressedSize $random) -gt [Math]::Floor($random.Length * 98 / 100)) 'random payload not incompressible'
    }
    test_run_id_rejects_empty_unsafe_and_overlong_values = {
        foreach ($invalid in @('', '../old', 'run with spaces', ('x' * 82), [string][char]0)) {
            $null = Assert-Throws { Assert-RunId $invalid }
        }
        Assert-RunId 'ci-123.2_attempt'
    }
    test_frame_parser_accepts_exact_empty_and_single_byte_streams = {
        Assert-Equal ([byte[]]::new(0)) (ConvertFrom-PortableMsZip $script:EmptyFrame 0)
        Assert-Equal ([byte[]]@(167)) (ConvertFrom-PortableMsZip $script:SingleFrame 1)
    }
    test_frame_parser_rejects_corruption_and_trailing_data = {
        $corruptions = [ordered]@{
            truncated_header = [byte[]]$script:EmptyFrame[0..22]
            bad_signature = [byte[]]$script:EmptyFrame.Clone()
            bad_header_size = [byte[]]$script:EmptyFrame.Clone()
            bad_checksum = [byte[]]$script:EmptyFrame.Clone()
            bad_algorithm = [byte[]]$script:EmptyFrame.Clone()
            wrong_length = $script:SingleFrame
            wrong_block_size = [byte[]]$script:EmptyFrame.Clone()
            unexpected_trailing = [byte[]](@($script:EmptyFrame) + @(0))
        }
        $corruptions.bad_signature[0] = 0
        $corruptions.bad_header_size[4] = 1
        $corruptions.bad_checksum[6] = $corruptions.bad_checksum[6] -bxor 1
        $corruptions.bad_algorithm[7] = 3
        [BitConverter]::GetBytes([long]32767).CopyTo($corruptions.wrong_block_size, 16)
        foreach ($frame in $corruptions.Values) { $null = Assert-Throws { ConvertFrom-PortableMsZip $frame 0 } }
        $marker = [byte[]]$script:SingleFrame.Clone()
        $marker[28] = 88; $marker[29] = 88
        $deflate = [byte[]]$script:SingleFrame.Clone()
        $deflate[30] = 255
        foreach ($frame in @(
            ,([byte[]]$script:SingleFrame[0..($script:SingleFrame.Length - 2)])
            ,$marker
            ,([byte[]](@($script:SingleFrame) + @(0)))
            ,$deflate
        )) { $null = Assert-Throws { ConvertFrom-PortableMsZip $frame 1 } }
    }
    test_frame_parser_enforces_native_framed_block_limit = {
        [byte[]]$header = $script:EmptyFrame.Clone()
        [BitConverter]::GetBytes([long]32768).CopyTo($header, 8)
        Set-HeaderChecksum $header
        [byte[]]$atLimit = [byte[]]::new(24 + 4 + 32780)
        $header.CopyTo($atLimit, 0)
        [BitConverter]::GetBytes([uint]32780).CopyTo($atLimit, 24)
        $atLimit[28] = 67; $atLimit[29] = 75
        Test-Frame $atLimit 32768
        [byte[]]$oversized = [byte[]]::new($atLimit.Length + 1)
        $atLimit.CopyTo($oversized, 0)
        [BitConverter]::GetBytes([uint]32781).CopyTo($oversized, 24)
        $null = Assert-Throws { Test-Frame $oversized 32768 } 'exceeds the Windows Compression API limit'
    }
    test_native_validator_refuses_non_windows_hosts = {
        Invoke-WithMocks @{ 'Get-InteropPlatform' = { 'linux' } } {
            $null = Assert-Throws { New-WindowsNativeMsZip } 'requires Windows'
        }
    }
    test_windows_empty_decode_uses_non_null_one_byte_output = {
        $native = New-Api
        $script:Api.OnTransform = {
            param($compressor, $handle, $source, $size, $destination, $capacity)
            $script:Trace.Add(@('decompress', [int]$size.ToUInt64(), [int]$capacity.ToUInt64(), ($null -ne $destination -and $destination.Length -gt 0)))
            $script:Api.Written = [UIntPtr]0
        }
        Assert-Equal ([byte[]]::new(0)) ($native.Decompress($script:EmptyFrame, 0))
        Assert-Equal @(@('create', 2, $true), @('decompress', $script:EmptyFrame.Length, 1, $true), @('close', 11)) $script:Trace.ToArray()
    }
    test_native_empty_success_with_nonzero_written_length_is_rejected = {
        $native = New-Api
        $script:Api.Handle = [IntPtr]12
        $script:Api.OnTransform = {
            param($compressor, $handle, $source, $size, $destination, $capacity)
            $script:Api.Written = [UIntPtr]1
        }
        $null = Assert-Throws { $native.Decompress($script:EmptyFrame, 0) } 'native decoded length 1 != 0'
        Assert-Equal @('close', 12) $script:Trace[$script:Trace.Count - 1]
    }
    test_native_decode_error_reports_win32_failure_and_closes_handle = {
        $native = New-Api
        $script:Api.Handle = [IntPtr]13
        $script:Api.OnTransform = {
            param($compressor, $handle, $source, $size, $destination, $capacity)
            $script:Api.Success = $false
            $script:Api.ErrorCode = 13
        }
        $null = Assert-Throws { $native.Decompress($script:EmptyFrame, 0) } 'Win32 error 13' ([Pinget.Interop.NativeDecodeError])
        Assert-Equal @('close', 13) $script:Trace[$script:Trace.Count - 1]
    }
    test_native_empty_compression_control_uses_explicit_buffer = {
        $native = New-Api
        $script:Api.Handle = [IntPtr]14
        $script:Api.OnTransform = {
            param($compressor, $handle, $source, $size, $destination, $capacity)
            $script:Trace.Add(@('compress', [int]$size.ToUInt64(), [int]$capacity.ToUInt64(), ($null -ne $source -and $source.Length -gt 0)))
            $script:EmptyFrame.CopyTo($destination, 0)
            $script:Api.Written = [UIntPtr]$script:EmptyFrame.Length
        }
        Assert-Equal $script:EmptyFrame ($native.CompressReference([byte[]]::new(0)))
        Assert-Equal @(@('create', 2, $true), @('compress', 0, 4096, $true), @('close', 14)) $script:Trace.ToArray()
    }
    test_native_positive_control_checks_roundtrip_before_fixture_validation = {
        foreach ($producer in $script:Producers) {
            $directory = Join-Path $script:TestDirectory $producer
            $null = [IO.Directory]::CreateDirectory($directory)
            [IO.File]::WriteAllBytes((Join-Path $directory 'single_byte.mszip'), $script:SingleFrame)
        }
        $script:AssertedInput = $null
        $native = New-FakeNative {
            param($data, $size)
            if ([Text.Encoding]::ASCII.GetString($data) -ceq 'native' -and $size -eq 1) { return ,([byte[]]@(167)) }
            throw [Pinget.Interop.NativeDecodeError]::new('wrong control')
        } {
            param($data)
            $script:AssertedInput = $data
            return ,([Text.Encoding]::ASCII.GetBytes('native'))
        }
        $null = Test-NativePositiveControl $native $script:TestDirectory
        Assert-Equal ([byte[]]@(167)) $script:AssertedInput
        $native | Add-Member ScriptMethod Decompress { param($data, $size) return ,([byte[]]@(0)) } -Force
        $null = Assert-Throws { Test-NativePositiveControl $native $script:TestDirectory } 'native nonempty control failed'
    }
    test_native_corrupted_controls_are_all_exercised = {
        $script:Trace = [Collections.Generic.List[object]]::new()
        $native = New-FakeNative {
            param($data, $size)
            $script:Trace.Add(@($data, $size))
            throw [Pinget.Interop.NativeDecodeError]::new('invalid native stream')
        }
        Assert-NativeMalformedReference $native $script:SingleFrame 1
        Assert-Equal 2 $script:Trace.Count
        Assert-Equal @(1, 1) @($script:Trace | ForEach-Object { $_[1] })
        Assert-Equal ([byte[]]$script:SingleFrame[0..22]) $script:Trace[0][0]
        Assert-Interop ($script:SingleFrame[0] -ne $script:Trace[1][0][0]) 'signature was not damaged'
    }
    test_native_decoder_accepting_malformed_reference_is_a_failure = {
        $native = New-FakeNative { param($data, $size) return ,([byte[]]::new(0)) }
        $null = Assert-Throws { Assert-NativeMalformedReference $native $script:SingleFrame 1 } 'accepted malformed control: truncated_header'
    }
    test_native_zero_input_probe_rejects_a_nonempty_result = {
        $native = New-FakeNative { param($data, $size) return ,([Text.Encoding]::ASCII.GetBytes('x')) }
        $null = Assert-Throws { Get-NativeZeroInputProbe $native } 'zero-input probe returned nonempty'
    }
    test_native_empty_control_requires_compress_error_87 = {
        $script:Trace = [Collections.Generic.List[object]]::new()
        $native = New-FakeNative { param($data, $size) return ,([byte[]]::new(0)) } {
            param($data)
            $script:Trace.Add($data)
            throw [Pinget.Interop.NativeCompressionError]::new(87)
        }
        Test-NativeEmptyRejection $native
        Assert-Equal 1 $script:Trace.Count
        Assert-Equal ([byte[]]::new(0)) $script:Trace[0]
        $native | Add-Member ScriptMethod CompressReference { param($data) throw [Pinget.Interop.NativeCompressionError]::new(5) } -Force
        $null = Assert-Throws { Test-NativeEmptyRejection $native } 'unexpected native empty-input failure'
        $native | Add-Member ScriptMethod CompressReference { param($data) throw [Pinget.Interop.NativeReferenceUnavailable]::new('compressor creation failed') } -Force
        $null = Assert-Throws { Test-NativeEmptyRejection $native } 'compressor creation failed' ([Pinget.Interop.NativeReferenceUnavailable])
        $native | Add-Member ScriptMethod CompressReference { param($data) return ,([Text.Encoding]::ASCII.GetBytes('unexpected')) } -Force
        $null = Assert-Throws { Test-NativeEmptyRejection $native } 'unexpectedly accepted empty input'
    }
    test_native_validator_runs_controls_and_fixture_matrix = {
        $script:Native = New-FakeNative { param($data, $size) return ,([byte[]]@(167)) }
        $script:Trace = [Collections.Generic.List[object]]::new()
        Invoke-WithMocks @{
            'New-WindowsNativeMsZip' = { $script:Native }
            'Test-NativePositiveControl' = {
                param($native, $root)
                Assert-Interop ([object]::ReferenceEquals($native, $script:Native)) 'wrong positive-control native'
                Assert-Equal 'unused' $root
                $script:Trace.Add('positive')
                return ,$script:SingleFrame
            }
            'Assert-NativeMalformedReference' = {
                param($native, $valid, $size)
                Assert-Interop ([object]::ReferenceEquals($native, $script:Native)) 'wrong malformed-control native'
                Assert-Equal $script:SingleFrame $valid
                Assert-Equal 1 $size
                $script:Trace.Add('malformed')
            }
            'Test-NativeEmptyRejection' = {
                param($native)
                Assert-Interop ([object]::ReferenceEquals($native, $script:Native)) 'wrong empty-control native'
                $script:Trace.Add('empty')
            }
            'Get-NativeZeroInputProbe' = { param($native) $script:Trace.Add('probe'); return 'rejected' }
            'Test-FixtureTree' = {
                param($root, $runId, $decoder)
                Assert-Equal 'unused' $root
                Assert-Equal 'run-42' $runId
                Assert-Equal ([byte[]]@(167)) (& $decoder $script:SingleFrame 1)
                $script:Trace.Add('matrix')
            }
        } { Invoke-ValidateWindows 'unused' 'run-42' }
        Assert-Equal @('positive', 'malformed', 'empty', 'probe', 'matrix') $script:Trace.ToArray()
    }
    test_rust_results_require_all_named_passing_tests = {
        $text = "running 3 tests`n" + (($script:RustTests | Sort-Object | ForEach-Object { "test $_ ... ok`n" }) -join '')
        $text += "test result: ok. 3 passed; 0 failed; 0 ignored; 4 measured; 0 filtered out`n"
        Test-RustResults $text
        foreach ($invalid in @('test result: ok. 0 passed; 0 failed; 0 ignored;', $text.Replace('3 passed', '1 passed'))) {
            $null = Assert-Throws { Test-RustResults $invalid }
        }
    }
    test_trx_requires_exact_discovery_counters_and_passing_outcomes = {
        $report = Join-Path $script:TestDirectory 'test.trx'
        $results = (($script:DotnetTests | Sort-Object | ForEach-Object { "<UnitTestResult testName=`"$_`" outcome=`"Passed`" />" }) -join '')
        $text = '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">' +
            "<Results>$results</Results>" +
            '<ResultSummary><Counters total="3" executed="3" passed="3" failed="0" notExecuted="0" /></ResultSummary></TestRun>'
        [IO.File]::WriteAllText($report, $text)
        Test-DotnetReport $report
        foreach ($invalid in @(
            $text.Replace('passed="3"', 'passed="0"')
            ([regex]::new('outcome="Passed"')).Replace($text, 'outcome="Failed"', 1)
            $text.Replace($results, '')
        )) {
            [IO.File]::WriteAllText($report, $invalid)
            $null = Assert-Throws { Test-DotnetReport $report }
        }
    }
}

$ownedDirectory = Join-Path ([IO.Path]::GetFullPath($Artifacts)) ("mszip-unit-" + [guid]::NewGuid().ToString('N'))
$failed = 0
try {
    $null = [IO.Directory]::CreateDirectory($ownedDirectory)
    foreach ($entry in $tests.GetEnumerator()) {
        $script:TestDirectory = Join-Path $ownedDirectory $entry.Key
        $null = [IO.Directory]::CreateDirectory($script:TestDirectory)
        try {
            & $entry.Value
            Write-Host "PASS $($entry.Key)"
        } catch {
            $failed++
            [Console]::Error.WriteLine("FAIL $($entry.Key): $(Get-InteropException $_)")
            [Console]::Error.WriteLine($_.ScriptStackTrace)
        } finally {
            [IO.Directory]::Delete($script:TestDirectory, $true)
        }
    }
} finally {
    if ([IO.Directory]::Exists($ownedDirectory)) { [IO.Directory]::Delete($ownedDirectory, $true) }
}
Write-Host "$($tests.Count) tests; $($tests.Count - $failed) passed; $failed failed."
if ($failed -ne 0) { exit 1 }
exit 0
