# Get the ID and security principal of the current user account
 $myWindowsID=[System.Security.Principal.WindowsIdentity]::GetCurrent()
 $myWindowsPrincipal=new-object System.Security.Principal.WindowsPrincipal($myWindowsID)

 # Get the security principal for the Administrator role
 $adminRole=[System.Security.Principal.WindowsBuiltInRole]::Administrator

 # Check to see if we are currently running "as Administrator"
 if ($myWindowsPrincipal.IsInRole($adminRole))
    {
    # We are running "as Administrator" - so change the title and background color to indicate this
    $Host.UI.RawUI.WindowTitle = $myInvocation.MyCommand.Definition + "(Elevated)"
    $Host.UI.RawUI.BackgroundColor = "DarkBlue"
    clear-host
    }
 else
    {
    # We are not running "as Administrator" - so relaunch as administrator

    # Create a new process object that starts PowerShell
    $newProcess = new-object System.Diagnostics.ProcessStartInfo "PowerShell";

    # Specify the current script path and name as a parameter
    $newProcess.Arguments = $myInvocation.MyCommand.Definition;

    # Indicate that the process should be elevated
    $newProcess.Verb = "runas";

    # Start the new process
    [System.Diagnostics.Process]::Start($newProcess);

    # Exit from the current, unelevated, process
    exit
    }

 # Run your code that needs to be elevated here##############################################################################
## Add-SqlServerStartupParameters
## by Eric Humphrey (http://www.erichumphrey.com/category/powershell/)
## http://www.sqlservercentral.com/blogs/erichumphrey/archive/2011/3/31/change-sql-startup-parameters-with-powershell.aspx
## https://github.com/mmessano/PowerShell/blob/master/Add-SqlServerStartupParameter.ps1



$hklmRootNode = "HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server"

#Get all instances
$props = Get-ItemProperty "$hklmRootNode\Instance Names\SQL"
$instances = $props.psobject.properties | ?{$_.Value -like 'MSSQL*'} | select Value
if($SQLinstance)
{
    $instances = $SQLinstance
}



$instances | %{
    $inst = $_.Value;
    $inst
    $regKey = "$hklmRootNode\$inst\MSSQLServer\Parameters";
    $version = $inst.Replace('MSSQL','');
    $SQLVersion = $version.substring(0,$version.indexof('.'));
    $traceflags = @();
	if($SQLVersion -lt 13)
    {
		$traceflags += '-T1117';
        $traceflags += '-T1118';
		$traceflags += '-T2371';
		$traceflags += '-T174';
	}
    if($SQLVersion -ge 10)
    {
        $traceflags += '-T3226';
        $traceflags += '-T1204';
        $traceflags += '-T1222';
        #$traceflags += '-T1224';
		$traceflags += '-T4199';
		$traceflags += '-T3226';
		$traceflags += '-T1800';
    };
    if($SQLVersion -ge 11)
    {
        $traceflags += '-T2453';
        $traceflags += '-T9488';
    };
    if($SQLVersion -ge 13)
    {
        $traceflags += '-T9567';
    };

    foreach($StartupParameter in $traceflags)
    {

    $props = Get-ItemProperty $regKey;
    $params = $props.psobject.properties | ?{$_.Name -like 'SQLArg*'} | select Name, Value;
    #$params | ft -AutoSize
    $hasFlag = $false;
    foreach ($param in $params) {
        if($param.Value -eq $StartupParameter) {
            $hasFlag = $true;
            break;
        };
    };
    if (-not $hasFlag) {
        "Adding $StartupParameter";
        $newRegProp = "SQLArg"+($params.Count);
        Set-ItemProperty -Path $regKey -Name $newRegProp -Value $StartupParameter;
    } else {
        "$StartupParameter already set";
    };
    };
}

 Read-Host "Traceflags added. Press any key to continue..."
 $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")


