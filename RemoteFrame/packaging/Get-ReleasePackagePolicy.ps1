param([ValidateSet('All','Managed','Worker','LibreOffice','Integration')][string]$Group = 'All')
$managed = @('RemoteFrame.Core','RemoteFrame.Rendering','RemoteFrame.AspNetCore')
$worker = @('RemoteFrame.Office.Worker.Runtime.win-x64','RemoteFrame.Office.Windows')
$libre = @('RemoteFrame.LibreOffice.Runtime.win-x64')
$integration = @('RemoteFrame.Office.Runtime.win-x64','RemoteFrame.Office.Windows.WithLibreOffice')
$order = @($managed) + $worker[0] + $libre + $integration[0] + $worker[1] + $integration[1]
$ids = switch ($Group) {
    Managed { $managed }; Worker { $worker }; LibreOffice { $libre }; Integration { $integration }; All { $order }
}
$parts = switch ($Group) {
    Worker { $worker[0] }; LibreOffice { $libre[0] }; All { $worker[0]; $libre[0] }
}
$external = switch ($Group) {
    Worker { 'RemoteFrame.AspNetCore' }
    Integration { $worker; $libre }
}
[pscustomobject]@{ Group=$Group; Required=@($ids); PartPrefixes=@($parts | Where-Object { $_ }); ExternalDependencies=@($external | Where-Object { $_ }); Order=$order }
