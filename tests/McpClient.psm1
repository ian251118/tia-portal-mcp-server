# Minimal MCP stdio client for testing TiaMcpServer.exe from PowerShell.
#
#   Import-Module .\tests\McpClient.psm1
#   $s = Start-McpServer -Exe .\bin\Release\TiaMcpServer.exe
#   (Invoke-McpTool $s tia_state).Data
#   Stop-McpServer $s

Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;

public sealed class McpProcess : IDisposable
{
    private readonly Process _p;
    private readonly StringBuilder _stderr = new StringBuilder();
    private readonly UTF8Encoding _utf8 = new UTF8Encoding(false);

    public McpProcess(string exe, string arguments)
    {
        var psi = new ProcessStartInfo(exe, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        _p = new Process { StartInfo = psi };
        _p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (_stderr) _stderr.AppendLine(e.Data); };
        _p.Start();
        _p.BeginErrorReadLine();
    }

    public bool HasExited { get { return _p.HasExited; } }

    public string Stderr { get { lock (_stderr) return _stderr.ToString(); } }

    // Sends one JSON-RPC line and returns the response line (null for notifications).
    public string Send(string json, bool expectResponse, int timeoutMs)
    {
        var bytes = _utf8.GetBytes(json + "\n");
        _p.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
        _p.StandardInput.BaseStream.Flush();
        if (!expectResponse) return null;

        Task<string> read = _p.StandardOutput.ReadLineAsync();
        if (!read.Wait(timeoutMs))
            throw new TimeoutException("No response from the MCP server within " + (timeoutMs / 1000) + " s.");
        return read.Result;
    }

    public void Dispose()
    {
        try { _p.StandardInput.Close(); } catch { }
        try { if (!_p.WaitForExit(5000)) _p.Kill(); } catch { }
        _p.Dispose();
    }
}
'@

$script:NextId = 0

function Start-McpServer {
    param(
        [Parameter(Mandatory)] [string] $Exe,
        [string[]] $Arguments = @()
    )
    $exePath = (Resolve-Path $Exe).Path
    $argLine = ($Arguments | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' '
    $server = New-Object McpProcess($exePath, $argLine)

    $init = Invoke-Mcp $server 'initialize' @{
        protocolVersion = '2025-06-18'
        capabilities    = @{}
        clientInfo      = @{ name = 'McpClient.psm1'; version = '1.0' }
    }
    [void] $server.Send('{"jsonrpc":"2.0","method":"notifications/initialized"}', $false, 0)
    Add-Member -InputObject $server -NotePropertyName Initialize -NotePropertyValue $init.result -Force
    return $server
}

# NOTE on arguments: pass file content read with [IO.File]::ReadAllText(), never with
# Get-Content. A Get-Content string carries PSPath/PSDrive/PSProvider note properties and
# Windows PowerShell's ConvertTo-Json serializes that whole object graph instead of the
# text (taking minutes to do it).
function Invoke-Mcp {
    param(
        [Parameter(Mandatory, Position = 0)] $Server,
        [Parameter(Mandatory, Position = 1)] [string] $Method,
        [Parameter(Position = 2)] $Params = @{},
        [int] $TimeoutSec = 60
    )
    $script:NextId++
    $request = @{ jsonrpc = '2.0'; id = $script:NextId; method = $Method; params = $Params }
    $json = ConvertTo-Json -InputObject $request -Depth 40 -Compress
    $line = $Server.Send($json, $true, $TimeoutSec * 1000)
    if ($null -eq $line) { throw "The MCP server closed its output. stderr:`n$($Server.Stderr)" }
    return ConvertFrom-Json $line
}

function Invoke-McpTool {
    param(
        [Parameter(Mandatory, Position = 0)] $Server,
        [Parameter(Mandatory, Position = 1)] [string] $Name,
        [Parameter(Position = 2)] [hashtable] $Arguments = @{},
        [int] $TimeoutSec = 180
    )
    $response = Invoke-Mcp $Server 'tools/call' @{ name = $Name; arguments = $Arguments } -TimeoutSec $TimeoutSec
    if ($response.error) {
        return [pscustomobject]@{ IsError = $true; Text = $response.error.message; Data = $null; RpcError = $true }
    }
    $text = $response.result.content[0].text
    $data = $null
    if (-not $response.result.isError) {
        try { $data = ConvertFrom-Json $text } catch { }
    }
    return [pscustomobject]@{ IsError = [bool]$response.result.isError; Text = $text; Data = $data; RpcError = $false }
}

function Stop-McpServer {
    param([Parameter(Mandatory, Position = 0)] $Server)
    $Server.Dispose()
}

Export-ModuleMember -Function Start-McpServer, Invoke-Mcp, Invoke-McpTool, Stop-McpServer
