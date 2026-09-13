"""Read-only current environment; no feature, token, policy or session changes."""
import argparse, json, platform, subprocess
from pathlib import Path
from datetime import datetime, timezone
def main():
    p=argparse.ArgumentParser();p.add_argument('--output',type=Path,required=True);a=p.parse_args();assert not a.output.exists();a.output.mkdir()
    script="""[pscustomobject]@{
Time=[DateTime]::UtcNow.ToString('o')
Identity=[Security.Principal.WindowsIdentity]::GetCurrent().Name
Elevated=([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Computer=Get-CimInstance Win32_ComputerSystem | Select-Object Manufacturer,Model,HypervisorPresent
SandboxExists=Test-Path C:/Windows/System32/WindowsSandbox.exe
VirtualizationCommands=@(Get-Command Get-VM,vmrun.exe,VBoxManage.exe -ErrorAction SilentlyContinue | Select-Object Name,Source)
Services=@(Get-Service vmcompute,vmms -ErrorAction SilentlyContinue | Select-Object Name,Status)
} | ConvertTo-Json -Depth 5"""
    commands=[('environment',['pwsh','-NoProfile','-Command',script]),('token',['whoami','/all']),('sessions',['quser']),('hcs',['hcsdiag','list']),('wpr',['wpr','-status']),('etw-sessions',['logman','query','-ets'])]
    records=[]
    for name,cmd in commands:
        began=datetime.now(timezone.utc).isoformat()
        r=subprocess.run(cmd,capture_output=True,timeout=30,creationflags=0x08000000)
        with (a.output/(name+'.stdout')).open('xb') as f:f.write(r.stdout)
        with (a.output/(name+'.stderr')).open('xb') as f:f.write(r.stderr)
        records.append(dict(Name=name,Command=cmd,ExitCode=r.returncode,ExitHex='0x%08X'%(r.returncode&4294967295),StartedUtc=began,EndedUtc=datetime.now(timezone.utc).isoformat()))
    with (a.output/'commands.json').open('x',encoding='utf-8') as f:json.dump(dict(OS=platform.platform(),Commands=records,ReadOnly=True),f,indent=2)
    print((a.output/'environment.stdout').read_text(encoding='utf-8'))
if __name__=='__main__':main()
