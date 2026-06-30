
BEGIN TRANSACTION
DECLARE @ReturnCode INT
SELECT @ReturnCode = 0
IF NOT EXISTS (SELECT name FROM msdb.dbo.syscategories WHERE name=N'Data Collector' AND category_class=1)
BEGIN
EXEC @ReturnCode = msdb.dbo.sp_add_category @class=N'JOB', @type=N'LOCAL', @name=N'Data Collector'
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback

END

DECLARE @jobId BINARY(16)
EXEC @ReturnCode =  msdb.dbo.sp_add_job @job_name=N'SQLWATCH-INTERNAL-ACTIONS', 
		@enabled=1, 
		@notify_level_eventlog=2, 
		@notify_level_email=0, 
		@notify_level_netsend=0, 
		@notify_level_page=0, 
		@delete_level=0, 
		@description=N'https://sqlwatch.io', 
		@category_name=N'Data Collector', 
		@owner_login_name=N'sa', @job_id = @jobId OUTPUT
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobstep @job_id=@jobId, @step_name=N'Process Actions', 
		@step_id=1, 
		@cmdexec_success_code=0, 
		@on_success_action=1, 
		@on_success_step_id=0, 
		@on_fail_action=2, 
		@on_fail_step_id=0, 
		@retry_attempts=0, 
		@retry_interval=0, 
		@os_run_priority=0, @subsystem=N'PowerShell', 
		@command=N'
$output = "x"
while ($output -ne $null) { 
	$output = Invoke-SqlCmd -ServerInstance "MSI" -Database SQLWATCH -MaxCharLength 2147483647 -Query "exec [dbo].[usp_sqlwatch_internal_action_queue_get_next]"

	$status = ""
	$queue_item_id = $output.queue_item_id
    $operation = ""
	$ErrorOutput = ""
	$MsgType = "OK"
	
	if ( $output -ne $null) {
		if ( $output.action_exec_type -eq "T-SQL" ) {
			try {
				$ErrorOutput = Invoke-SqlCmd -ServerInstance "MSI" -Database SQLWATCH -ErrorAction "Stop" -Query $output.action_exec -MaxCharLength 2147483647
			}
			catch {
				$ErrorOutput = $error[0] -replace "''", "''''"
				$MsgType = "ERROR"
			}
		}

		if ( $output.action_exec_type -eq "PowerShell" ) {
			try {
				$ErrorOutput = Invoke-Expression $output.action_exec -ErrorAction "Stop" 
			}
			catch {
				$ErrorOutput = $_.Exception.Message -replace "''", "''''"
				$MsgType = "ERROR"
			}
		}
		Invoke-SqlCmd -ServerInstance "MSI" -Database SQLWATCH -ErrorAction "Stop" -Query "exec [dbo].[usp_sqlwatch_internal_action_queue_update]
					@queue_item_id = $queue_item_id,
					@error = ''$ErrorOutput'',
					@exec_status = ''$MsgType''"
	}
}', 
		@database_name=N'SQLWATCH', 
		@flags=0
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_update_job @job_id = @jobId, @start_step_id = 1
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobschedule @job_id=@jobId, @name=N'SQLWATCH-INTERNAL-ACTIONS', 
		@enabled=1, 
		@freq_type=4, 
		@freq_interval=1, 
		@freq_subday_type=2, 
		@freq_subday_interval=15, 
		@freq_relative_interval=0, 
		@freq_recurrence_factor=0, 
		@active_start_date=20180101, 
		@active_end_date=99991231, 
		@active_start_time=2, 
		@active_end_time=235959, 
		@schedule_uid=N'0275259f-344a-4dc8-9756-9ebd0fedc5c1'
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobserver @job_id = @jobId, @server_name = N'(local)'
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
COMMIT TRANSACTION
GOTO EndSave
QuitWithRollback:
    IF (@@TRANCOUNT > 0) ROLLBACK TRANSACTION
EndSave:
GO


BEGIN TRANSACTION
DECLARE @ReturnCode INT
SELECT @ReturnCode = 0
IF NOT EXISTS (SELECT name FROM msdb.dbo.syscategories WHERE name=N'Data Collector' AND category_class=1)
BEGIN
EXEC @ReturnCode = msdb.dbo.sp_add_category @class=N'JOB', @type=N'LOCAL', @name=N'Data Collector'
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback

END

DECLARE @jobId BINARY(16)
EXEC @ReturnCode =  msdb.dbo.sp_add_job @job_name=N'SQLWATCH-INTERNAL-CHECKS', 
		@enabled=1, 
		@notify_level_eventlog=2, 
		@notify_level_email=0, 
		@notify_level_netsend=0, 
		@notify_level_page=0, 
		@delete_level=0, 
		@description=N'https://sqlwatch.io', 
		@category_name=N'Data Collector', 
		@owner_login_name=N'sa', @job_id = @jobId OUTPUT
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobstep @job_id=@jobId, @step_name=N'dbo.usp_sqlwatch_internal_process_checks', 
		@step_id=1, 
		@cmdexec_success_code=0, 
		@on_success_action=1, 
		@on_success_step_id=0, 
		@on_fail_action=2, 
		@on_fail_step_id=0, 
		@retry_attempts=0, 
		@retry_interval=0, 
		@os_run_priority=0, @subsystem=N'TSQL', 
		@command=N'exec dbo.usp_sqlwatch_internal_process_checks', 
		@database_name=N'SQLWATCH', 
		@flags=0
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_update_job @job_id = @jobId, @start_step_id = 1
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobschedule @job_id=@jobId, @name=N'SQLWATCH-INTERNAL-CHECKS', 
		@enabled=1, 
		@freq_type=4, 
		@freq_interval=1, 
		@freq_subday_type=4, 
		@freq_subday_interval=1, 
		@freq_relative_interval=0, 
		@freq_recurrence_factor=0, 
		@active_start_date=20180101, 
		@active_end_date=99991231, 
		@active_start_time=43, 
		@active_end_time=235959, 
		@schedule_uid=N'24699e32-cd8a-428f-91a0-ea1b415d41fe'
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobserver @job_id = @jobId, @server_name = N'(local)'
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
COMMIT TRANSACTION
GOTO EndSave
QuitWithRollback:
    IF (@@TRANCOUNT > 0) ROLLBACK TRANSACTION
EndSave:
GO



BEGIN TRANSACTION
DECLARE @ReturnCode INT
SELECT @ReturnCode = 0
IF NOT EXISTS (SELECT name FROM msdb.dbo.syscategories WHERE name=N'Data Collector' AND category_class=1)
BEGIN
EXEC @ReturnCode = msdb.dbo.sp_add_category @class=N'JOB', @type=N'LOCAL', @name=N'Data Collector'
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback

END

DECLARE @jobId BINARY(16)
EXEC @ReturnCode =  msdb.dbo.sp_add_job @job_name=N'SQLWATCH-INTERNAL-CONFIG', 
		@enabled=1, 
		@notify_level_eventlog=2, 
		@notify_level_email=0, 
		@notify_level_netsend=0, 
		@notify_level_page=0, 
		@delete_level=0, 
		@description=N'https://sqlwatch.io', 
		@category_name=N'Data Collector', 
		@owner_login_name=N'sa', @job_id = @jobId OUTPUT
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobstep @job_id=@jobId, @step_name=N'dbo.usp_sqlwatch_internal_add_database', 
		@step_id=1, 
		@cmdexec_success_code=0, 
		@on_success_action=3, 
		@on_success_step_id=0, 
		@on_fail_action=3, 
		@on_fail_step_id=0, 
		@retry_attempts=0, 
		@retry_interval=0, 
		@os_run_priority=0, @subsystem=N'TSQL', 
		@command=N'exec dbo.usp_sqlwatch_internal_add_database', 
		@database_name=N'SQLWATCH', 
		@flags=0
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobstep @job_id=@jobId, @step_name=N'dbo.usp_sqlwatch_internal_add_master_file', 
		@step_id=2, 
		@cmdexec_success_code=0, 
		@on_success_action=3, 
		@on_success_step_id=0, 
		@on_fail_action=3, 
		@on_fail_step_id=0, 
		@retry_attempts=0, 
		@retry_interval=0, 
		@os_run_priority=0, @subsystem=N'TSQL', 
		@command=N'exec dbo.usp_sqlwatch_internal_add_master_file', 
		@database_name=N'SQLWATCH', 
		@flags=0
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobstep @job_id=@jobId, @step_name=N'dbo.usp_sqlwatch_internal_add_table', 
		@step_id=3, 
		@cmdexec_success_code=0, 
		@on_success_action=3, 
		@on_success_step_id=0, 
		@on_fail_action=3, 
		@on_fail_step_id=0, 
		@retry_attempts=0, 
		@retry_interval=0, 
		@os_run_priority=0, @subsystem=N'TSQL', 
		@command=N'exec dbo.usp_sqlwatch_internal_add_table', 
		@database_name=N'SQLWATCH', 
		@flags=0
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobstep @job_id=@jobId, @step_name=N'dbo.usp_sqlwatch_internal_add_job', 
		@step_id=4, 
		@cmdexec_success_code=0, 
		@on_success_action=3, 
		@on_success_step_id=0, 
		@on_fail_action=3, 
		@on_fail_step_id=0, 
		@retry_attempts=0, 
		@retry_interval=0, 
		@os_run_priority=0, @subsystem=N'TSQL', 
		@command=N'exec dbo.usp_sqlwatch_internal_add_job', 
		@database_name=N'SQLWATCH', 
		@flags=0
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobstep @job_id=@jobId, @step_name=N'dbo.usp_sqlwatch_internal_add_performance_counter', 
		@step_id=5, 
		@cmdexec_success_code=0, 
		@on_success_action=3, 
		@on_success_step_id=0, 
		@on_fail_action=3, 
		@on_fail_step_id=0, 
		@retry_attempts=0, 
		@retry_interval=0, 
		@os_run_priority=0, @subsystem=N'TSQL', 
		@command=N'exec dbo.usp_sqlwatch_internal_add_performance_counter', 
		@database_name=N'SQLWATCH', 
		@flags=0
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobstep @job_id=@jobId, @step_name=N'dbo.usp_sqlwatch_internal_add_memory_clerk', 
		@step_id=6, 
		@cmdexec_success_code=0, 
		@on_success_action=3, 
		@on_success_step_id=0, 
		@on_fail_action=3, 
		@on_fail_step_id=0, 
		@retry_attempts=0, 
		@retry_interval=0, 
		@os_run_priority=0, @subsystem=N'TSQL', 
		@command=N'exec dbo.usp_sqlwatch_internal_add_memory_clerk', 
		@database_name=N'SQLWATCH', 
		@flags=0
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobstep @job_id=@jobId, @step_name=N'dbo.usp_sqlwatch_internal_add_wait_type', 
		@step_id=7, 
		@cmdexec_success_code=0, 
		@on_success_action=3, 
		@on_success_step_id=0, 
		@on_fail_action=3, 
		@on_fail_step_id=0, 
		@retry_attempts=0, 
		@retry_interval=0, 
		@os_run_priority=0, @subsystem=N'TSQL', 
		@command=N'exec dbo.usp_sqlwatch_internal_add_wait_type', 
		@database_name=N'SQLWATCH', 
		@flags=0
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobstep @job_id=@jobId, @step_name=N'dbo.usp_sqlwatch_internal_expand_checks', 
		@step_id=8, 
		@cmdexec_success_code=0, 
		@on_success_action=3, 
		@on_success_step_id=0, 
		@on_fail_action=3, 
		@on_fail_step_id=0, 
		@retry_attempts=0, 
		@retry_interval=0, 
		@os_run_priority=0, @subsystem=N'TSQL', 
		@command=N'exec dbo.usp_sqlwatch_internal_expand_checks', 
		@database_name=N'SQLWATCH', 
		@flags=0
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobstep @job_id=@jobId, @step_name=N'dbo.usp_sqlwatch_internal_add_procedure', 
		@step_id=9, 
		@cmdexec_success_code=0, 
		@on_success_action=1, 
		@on_success_step_id=0, 
		@on_fail_action=2, 
		@on_fail_step_id=0, 
		@retry_attempts=0, 
		@retry_interval=0, 
		@os_run_priority=0, @subsystem=N'TSQL', 
		@command=N'exec dbo.usp_sqlwatch_internal_add_procedure', 
		@database_name=N'SQLWATCH', 
		@flags=0
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_update_job @job_id = @jobId, @start_step_id = 1
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobschedule @job_id=@jobId, @name=N'SQLWATCH-INTERNAL-CONFIG', 
		@enabled=1, 
		@freq_type=4, 
		@freq_interval=1, 
		@freq_subday_type=8, 
		@freq_subday_interval=1, 
		@freq_relative_interval=0, 
		@freq_recurrence_factor=0, 
		@active_start_date=20180101, 
		@active_end_date=99991231, 
		@active_start_time=26, 
		@active_end_time=235959, 
		@schedule_uid=N'e8af7c44-e62d-4a66-a9f3-db713673c84d'
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobserver @job_id = @jobId, @server_name = N'(local)'
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
COMMIT TRANSACTION
GOTO EndSave
QuitWithRollback:
    IF (@@TRANCOUNT > 0) ROLLBACK TRANSACTION
EndSave:
GO



BEGIN TRANSACTION
DECLARE @ReturnCode INT
SELECT @ReturnCode = 0
IF NOT EXISTS (SELECT name FROM msdb.dbo.syscategories WHERE name=N'Data Collector' AND category_class=1)
BEGIN
EXEC @ReturnCode = msdb.dbo.sp_add_category @class=N'JOB', @type=N'LOCAL', @name=N'Data Collector'
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback

END

DECLARE @jobId BINARY(16)
EXEC @ReturnCode =  msdb.dbo.sp_add_job @job_name=N'SQLWATCH-INTERNAL-RETENTION', 
		@enabled=1, 
		@notify_level_eventlog=2, 
		@notify_level_email=0, 
		@notify_level_netsend=0, 
		@notify_level_page=0, 
		@delete_level=0, 
		@description=N'https://sqlwatch.io', 
		@category_name=N'Data Collector', 
		@owner_login_name=N'sa', @job_id = @jobId OUTPUT
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobstep @job_id=@jobId, @step_name=N'dbo.usp_sqlwatch_internal_retention', 
		@step_id=1, 
		@cmdexec_success_code=0, 
		@on_success_action=3, 
		@on_success_step_id=0, 
		@on_fail_action=3, 
		@on_fail_step_id=0, 
		@retry_attempts=0, 
		@retry_interval=0, 
		@os_run_priority=0, @subsystem=N'TSQL', 
		@command=N'exec dbo.usp_sqlwatch_internal_retention', 
		@database_name=N'SQLWATCH', 
		@flags=0
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobstep @job_id=@jobId, @step_name=N'dbo.usp_sqlwatch_internal_purge_deleted_items', 
		@step_id=2, 
		@cmdexec_success_code=0, 
		@on_success_action=1, 
		@on_success_step_id=0, 
		@on_fail_action=2, 
		@on_fail_step_id=0, 
		@retry_attempts=0, 
		@retry_interval=0, 
		@os_run_priority=0, @subsystem=N'TSQL', 
		@command=N'exec dbo.usp_sqlwatch_internal_purge_deleted_items', 
		@database_name=N'SQLWATCH', 
		@flags=0
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_update_job @job_id = @jobId, @start_step_id = 1
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobschedule @job_id=@jobId, @name=N'SQLWATCH-INTERNAL-RETENTION', 
		@enabled=1, 
		@freq_type=4, 
		@freq_interval=1, 
		@freq_subday_type=8, 
		@freq_subday_interval=1, 
		@freq_relative_interval=0, 
		@freq_recurrence_factor=0, 
		@active_start_date=20180101, 
		@active_end_date=99991231, 
		@active_start_time=20, 
		@active_end_time=235959, 
		@schedule_uid=N'84aecd64-5246-4138-8bfc-7ce84c96dd00'
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobserver @job_id = @jobId, @server_name = N'(local)'
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
COMMIT TRANSACTION
GOTO EndSave
QuitWithRollback:
    IF (@@TRANCOUNT > 0) ROLLBACK TRANSACTION
EndSave:
GO



BEGIN TRANSACTION
DECLARE @ReturnCode INT
SELECT @ReturnCode = 0
IF NOT EXISTS (SELECT name FROM msdb.dbo.syscategories WHERE name=N'Data Collector' AND category_class=1)
BEGIN
EXEC @ReturnCode = msdb.dbo.sp_add_category @class=N'JOB', @type=N'LOCAL', @name=N'Data Collector'
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback

END

DECLARE @jobId BINARY(16)
EXEC @ReturnCode =  msdb.dbo.sp_add_job @job_name=N'SQLWATCH-INTERNAL-TRENDS', 
		@enabled=1, 
		@notify_level_eventlog=2, 
		@notify_level_email=0, 
		@notify_level_netsend=0, 
		@notify_level_page=0, 
		@delete_level=0, 
		@description=N'https://sqlwatch.io', 
		@category_name=N'Data Collector', 
		@owner_login_name=N'sa', @job_id = @jobId OUTPUT
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobstep @job_id=@jobId, @step_name=N'1 minute trend', 
		@step_id=1, 
		@cmdexec_success_code=0, 
		@on_success_action=3, 
		@on_success_step_id=0, 
		@on_fail_action=3, 
		@on_fail_step_id=0, 
		@retry_attempts=0, 
		@retry_interval=0, 
		@os_run_priority=0, @subsystem=N'TSQL', 
		@command=N'exec dbo.usp_sqlwatch_trend_perf_os_performance_counters @interval_minutes = 1, @valid_days = 7', 
		@database_name=N'SQLWATCH', 
		@flags=0
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobstep @job_id=@jobId, @step_name=N'5 minutes trend', 
		@step_id=2, 
		@cmdexec_success_code=0, 
		@on_success_action=3, 
		@on_success_step_id=0, 
		@on_fail_action=3, 
		@on_fail_step_id=0, 
		@retry_attempts=0, 
		@retry_interval=0, 
		@os_run_priority=0, @subsystem=N'TSQL', 
		@command=N'exec dbo.usp_sqlwatch_trend_perf_os_performance_counters @interval_minutes = 5, @valid_days = 90', 
		@database_name=N'SQLWATCH', 
		@flags=0
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobstep @job_id=@jobId, @step_name=N'60 minutes trend', 
		@step_id=3, 
		@cmdexec_success_code=0, 
		@on_success_action=1, 
		@on_success_step_id=0, 
		@on_fail_action=2, 
		@on_fail_step_id=0, 
		@retry_attempts=0, 
		@retry_interval=0, 
		@os_run_priority=0, @subsystem=N'TSQL', 
		@command=N'exec dbo.usp_sqlwatch_trend_perf_os_performance_counters @interval_minutes = 60, @valid_days = 720', 
		@database_name=N'SQLWATCH', 
		@flags=0
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_update_job @job_id = @jobId, @start_step_id = 1
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobschedule @job_id=@jobId, @name=N'SQLWATCH-INTERNAL-TRENDS', 
		@enabled=1, 
		@freq_type=4, 
		@freq_interval=1, 
		@freq_subday_type=4, 
		@freq_subday_interval=60, 
		@freq_relative_interval=0, 
		@freq_recurrence_factor=0, 
		@active_start_date=20180101, 
		@active_end_date=99991231, 
		@active_start_time=150, 
		@active_end_time=235959, 
		@schedule_uid=N'd21ae830-01e6-4f4c-9b50-9594cc0fea88'
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobserver @job_id = @jobId, @server_name = N'(local)'
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
COMMIT TRANSACTION
GOTO EndSave
QuitWithRollback:
    IF (@@TRANCOUNT > 0) ROLLBACK TRANSACTION
EndSave:
GO



BEGIN TRANSACTION
DECLARE @ReturnCode INT
SELECT @ReturnCode = 0
IF NOT EXISTS (SELECT name FROM msdb.dbo.syscategories WHERE name=N'Data Collector' AND category_class=1)
BEGIN
EXEC @ReturnCode = msdb.dbo.sp_add_category @class=N'JOB', @type=N'LOCAL', @name=N'Data Collector'
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback

END

DECLARE @jobId BINARY(16)
EXEC @ReturnCode =  msdb.dbo.sp_add_job @job_name=N'SQLWATCH-LOGGER-AG', 
		@enabled=1, 
		@notify_level_eventlog=2, 
		@notify_level_email=0, 
		@notify_level_netsend=0, 
		@notify_level_page=0, 
		@delete_level=0, 
		@description=N'https://sqlwatch.io', 
		@category_name=N'Data Collector', 
		@owner_login_name=N'sa', @job_id = @jobId OUTPUT
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobstep @job_id=@jobId, @step_name=N'dbo.usp_sqlwatch_logger_hadr_database_replica_states', 
		@step_id=1, 
		@cmdexec_success_code=0, 
		@on_success_action=1, 
		@on_success_step_id=0, 
		@on_fail_action=2, 
		@on_fail_step_id=0, 
		@retry_attempts=0, 
		@retry_interval=0, 
		@os_run_priority=0, @subsystem=N'TSQL', 
		@command=N'exec dbo.usp_sqlwatch_logger_hadr_database_replica_states', 
		@database_name=N'SQLWATCH', 
		@flags=0
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_update_job @job_id = @jobId, @start_step_id = 1
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobschedule @job_id=@jobId, @name=N'SQLWATCH-LOGGER-AG', 
		@enabled=1, 
		@freq_type=4, 
		@freq_interval=1, 
		@freq_subday_type=4, 
		@freq_subday_interval=1, 
		@freq_relative_interval=0, 
		@freq_recurrence_factor=0, 
		@active_start_date=20180101, 
		@active_end_date=99991231, 
		@active_start_time=12, 
		@active_end_time=235959, 
		@schedule_uid=N'1dbe8696-6a9a-468b-82bb-1f9711e57fe0'
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobserver @job_id = @jobId, @server_name = N'(local)'
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
COMMIT TRANSACTION
GOTO EndSave
QuitWithRollback:
    IF (@@TRANCOUNT > 0) ROLLBACK TRANSACTION
EndSave:
GO



BEGIN TRANSACTION
DECLARE @ReturnCode INT
SELECT @ReturnCode = 0
IF NOT EXISTS (SELECT name FROM msdb.dbo.syscategories WHERE name=N'Data Collector' AND category_class=1)
BEGIN
EXEC @ReturnCode = msdb.dbo.sp_add_category @class=N'JOB', @type=N'LOCAL', @name=N'Data Collector'
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback

END

DECLARE @jobId BINARY(16)
EXEC @ReturnCode =  msdb.dbo.sp_add_job @job_name=N'SQLWATCH-LOGGER-AGENT-HISTORY', 
		@enabled=1, 
		@notify_level_eventlog=2, 
		@notify_level_email=0, 
		@notify_level_netsend=0, 
		@notify_level_page=0, 
		@delete_level=0, 
		@description=N'https://sqlwatch.io', 
		@category_name=N'Data Collector', 
		@owner_login_name=N'sa', @job_id = @jobId OUTPUT
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobstep @job_id=@jobId, @step_name=N'dbo.usp_sqlwatch_logger_agent_job_history', 
		@step_id=1, 
		@cmdexec_success_code=0, 
		@on_success_action=1, 
		@on_success_step_id=0, 
		@on_fail_action=2, 
		@on_fail_step_id=0, 
		@retry_attempts=0, 
		@retry_interval=0, 
		@os_run_priority=0, @subsystem=N'TSQL', 
		@command=N'exec dbo.usp_sqlwatch_logger_agent_job_history', 
		@database_name=N'SQLWATCH', 
		@flags=0
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_update_job @job_id = @jobId, @start_step_id = 1
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobschedule @job_id=@jobId, @name=N'SQLWATCH-LOGGER-AGENT-HISTORY', 
		@enabled=1, 
		@freq_type=4, 
		@freq_interval=1, 
		@freq_subday_type=4, 
		@freq_subday_interval=10, 
		@freq_relative_interval=0, 
		@freq_recurrence_factor=0, 
		@active_start_date=20180101, 
		@active_end_date=99991231, 
		@active_start_time=0, 
		@active_end_time=235959, 
		@schedule_uid=N'da837bed-376e-4c8c-a1b0-5b7d0408e221'
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobserver @job_id = @jobId, @server_name = N'(local)'
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
COMMIT TRANSACTION
GOTO EndSave
QuitWithRollback:
    IF (@@TRANCOUNT > 0) ROLLBACK TRANSACTION
EndSave:
GO



BEGIN TRANSACTION
DECLARE @ReturnCode INT
SELECT @ReturnCode = 0
IF NOT EXISTS (SELECT name FROM msdb.dbo.syscategories WHERE name=N'Data Collector' AND category_class=1)
BEGIN
EXEC @ReturnCode = msdb.dbo.sp_add_category @class=N'JOB', @type=N'LOCAL', @name=N'Data Collector'
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback

END

DECLARE @jobId BINARY(16)
EXEC @ReturnCode =  msdb.dbo.sp_add_job @job_name=N'SQLWATCH-LOGGER-DISK-UTILISATION', 
		@enabled=1, 
		@notify_level_eventlog=2, 
		@notify_level_email=0, 
		@notify_level_netsend=0, 
		@notify_level_page=0, 
		@delete_level=0, 
		@description=N'https://sqlwatch.io', 
		@category_name=N'Data Collector', 
		@owner_login_name=N'sa', @job_id = @jobId OUTPUT
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobstep @job_id=@jobId, @step_name=N'dbo.usp_sqlwatch_logger_disk_utilisation', 
		@step_id=1, 
		@cmdexec_success_code=0, 
		@on_success_action=3, 
		@on_success_step_id=0, 
		@on_fail_action=3, 
		@on_fail_step_id=0, 
		@retry_attempts=0, 
		@retry_interval=0, 
		@os_run_priority=0, @subsystem=N'TSQL', 
		@command=N'exec dbo.usp_sqlwatch_logger_disk_utilisation', 
		@database_name=N'SQLWATCH', 
		@flags=0
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobstep @job_id=@jobId, @step_name=N'Get-WMIObject Win32_Volume', 
		@step_id=2, 
		@cmdexec_success_code=0, 
		@on_success_action=3, 
		@on_success_step_id=0, 
		@on_fail_action=3, 
		@on_fail_step_id=0, 
		@retry_attempts=0, 
		@retry_interval=0, 
		@os_run_priority=0, @subsystem=N'PowerShell', 
		@command=N'
#https://msdn.microsoft.com/en-us/library/aa394515(v=vs.85).aspx
#driveType 3 = Local disk
Get-WMIObject Win32_Volume | ?{$_.DriveType -eq 3 -And $_.Name -notlike "\\?\Volume*" } | %{
    $VolumeName = $_.Name
    $FreeSpace = $_.Freespace
    $Capacity = $_.Capacity
    $VolumeLabel = $_.Label
    $FileSystem = $_.Filesystem
    $BlockSize = $_.BlockSize
    Invoke-SqlCmd -ServerInstance "MSI" -Database SQLWATCH -Query "
	 exec [dbo].[usp_sqlwatch_internal_add_os_volume] 
		@volume_name = ''$VolumeName'', 
		@label = ''$VolumeLabel'', 
		@file_system = ''$FileSystem'', 
		@block_size = ''$BlockSize'';
	 exec [dbo].[usp_sqlwatch_logger_disk_utilisation_os_volume] 
		@volume_name = ''$VolumeName'',
		@volume_free_space_bytes = $FreeSpace,
		@volume_total_space_bytes = $Capacity
    " 
}', 
		@database_name=N'SQLWATCH', 
		@flags=0
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobstep @job_id=@jobId, @step_name=N'dbo.usp_sqlwatch_logger_disk_utilisation_table', 
		@step_id=3, 
		@cmdexec_success_code=0, 
		@on_success_action=1, 
		@on_success_step_id=0, 
		@on_fail_action=2, 
		@on_fail_step_id=0, 
		@retry_attempts=0, 
		@retry_interval=0, 
		@os_run_priority=0, @subsystem=N'TSQL', 
		@command=N'exec dbo.usp_sqlwatch_logger_disk_utilisation_table', 
		@database_name=N'SQLWATCH', 
		@flags=0
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_update_job @job_id = @jobId, @start_step_id = 1
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobschedule @job_id=@jobId, @name=N'SQLWATCH-LOGGER-DISK-UTILISATION', 
		@enabled=1, 
		@freq_type=4, 
		@freq_interval=1, 
		@freq_subday_type=8, 
		@freq_subday_interval=1, 
		@freq_relative_interval=0, 
		@freq_recurrence_factor=0, 
		@active_start_date=20180101, 
		@active_end_date=99991231, 
		@active_start_time=437, 
		@active_end_time=235959, 
		@schedule_uid=N'1e8991bd-1daf-4f64-be5a-dd5dab35e49f'
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobserver @job_id = @jobId, @server_name = N'(local)'
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
COMMIT TRANSACTION
GOTO EndSave
QuitWithRollback:
    IF (@@TRANCOUNT > 0) ROLLBACK TRANSACTION
EndSave:
GO



BEGIN TRANSACTION
DECLARE @ReturnCode INT
SELECT @ReturnCode = 0
IF NOT EXISTS (SELECT name FROM msdb.dbo.syscategories WHERE name=N'Data Collector' AND category_class=1)
BEGIN
EXEC @ReturnCode = msdb.dbo.sp_add_category @class=N'JOB', @type=N'LOCAL', @name=N'Data Collector'
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback

END

DECLARE @jobId BINARY(16)
EXEC @ReturnCode =  msdb.dbo.sp_add_job @job_name=N'SQLWATCH-LOGGER-INDEXES', 
		@enabled=1, 
		@notify_level_eventlog=2, 
		@notify_level_email=0, 
		@notify_level_netsend=0, 
		@notify_level_page=0, 
		@delete_level=0, 
		@description=N'https://sqlwatch.io', 
		@category_name=N'Data Collector', 
		@owner_login_name=N'sa', @job_id = @jobId OUTPUT
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobstep @job_id=@jobId, @step_name=N'dbo.usp_sqlwatch_internal_add_index', 
		@step_id=1, 
		@cmdexec_success_code=0, 
		@on_success_action=3, 
		@on_success_step_id=0, 
		@on_fail_action=3, 
		@on_fail_step_id=0, 
		@retry_attempts=0, 
		@retry_interval=0, 
		@os_run_priority=0, @subsystem=N'TSQL', 
		@command=N'exec dbo.usp_sqlwatch_internal_add_index', 
		@database_name=N'SQLWATCH', 
		@flags=0
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobstep @job_id=@jobId, @step_name=N'dbo.usp_sqlwatch_internal_add_index_missing', 
		@step_id=2, 
		@cmdexec_success_code=0, 
		@on_success_action=3, 
		@on_success_step_id=0, 
		@on_fail_action=3, 
		@on_fail_step_id=0, 
		@retry_attempts=0, 
		@retry_interval=0, 
		@os_run_priority=0, @subsystem=N'TSQL', 
		@command=N'exec dbo.usp_sqlwatch_internal_add_index_missing', 
		@database_name=N'SQLWATCH', 
		@flags=0
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobstep @job_id=@jobId, @step_name=N'dbo.usp_sqlwatch_logger_missing_index_stats', 
		@step_id=3, 
		@cmdexec_success_code=0, 
		@on_success_action=3, 
		@on_success_step_id=0, 
		@on_fail_action=3, 
		@on_fail_step_id=0, 
		@retry_attempts=0, 
		@retry_interval=0, 
		@os_run_priority=0, @subsystem=N'TSQL', 
		@command=N'exec dbo.usp_sqlwatch_logger_missing_index_stats', 
		@database_name=N'SQLWATCH', 
		@flags=0
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobstep @job_id=@jobId, @step_name=N'dbo.usp_sqlwatch_logger_index_usage_stats', 
		@step_id=4, 
		@cmdexec_success_code=0, 
		@on_success_action=3, 
		@on_success_step_id=0, 
		@on_fail_action=3, 
		@on_fail_step_id=0, 
		@retry_attempts=0, 
		@retry_interval=0, 
		@os_run_priority=0, @subsystem=N'TSQL', 
		@command=N'exec dbo.usp_sqlwatch_logger_index_usage_stats', 
		@database_name=N'SQLWATCH', 
		@flags=0
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobstep @job_id=@jobId, @step_name=N'dbo.usp_sqlwatch_logger_index_histogram', 
		@step_id=5, 
		@cmdexec_success_code=0, 
		@on_success_action=1, 
		@on_success_step_id=0, 
		@on_fail_action=2, 
		@on_fail_step_id=0, 
		@retry_attempts=0, 
		@retry_interval=0, 
		@os_run_priority=0, @subsystem=N'TSQL', 
		@command=N'exec dbo.usp_sqlwatch_logger_index_histogram', 
		@database_name=N'SQLWATCH', 
		@flags=0
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_update_job @job_id = @jobId, @start_step_id = 1
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobschedule @job_id=@jobId, @name=N'SQLWATCH-LOGGER-INDEXES', 
		@enabled=1, 
		@freq_type=4, 
		@freq_interval=1, 
		@freq_subday_type=1, 
		@freq_subday_interval=24, 
		@freq_relative_interval=0, 
		@freq_recurrence_factor=0, 
		@active_start_date=20180101, 
		@active_end_date=99991231, 
		@active_start_time=1500, 
		@active_end_time=235959, 
		@schedule_uid=N'225c02e0-535a-436a-800c-8445f8290be7'
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobserver @job_id = @jobId, @server_name = N'(local)'
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
COMMIT TRANSACTION
GOTO EndSave
QuitWithRollback:
    IF (@@TRANCOUNT > 0) ROLLBACK TRANSACTION
EndSave:
GO




BEGIN TRANSACTION
DECLARE @ReturnCode INT
SELECT @ReturnCode = 0
IF NOT EXISTS (SELECT name FROM msdb.dbo.syscategories WHERE name=N'Data Collector' AND category_class=1)
BEGIN
EXEC @ReturnCode = msdb.dbo.sp_add_category @class=N'JOB', @type=N'LOCAL', @name=N'Data Collector'
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback

END

DECLARE @jobId BINARY(16)
EXEC @ReturnCode =  msdb.dbo.sp_add_job @job_name=N'SQLWATCH-LOGGER-PERFORMANCE', 
		@enabled=1, 
		@notify_level_eventlog=2, 
		@notify_level_email=0, 
		@notify_level_netsend=0, 
		@notify_level_page=0, 
		@delete_level=0, 
		@description=N'https://sqlwatch.io', 
		@category_name=N'Data Collector', 
		@owner_login_name=N'sa', @job_id = @jobId OUTPUT
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobstep @job_id=@jobId, @step_name=N'dbo.usp_sqlwatch_logger_performance', 
		@step_id=1, 
		@cmdexec_success_code=0, 
		@on_success_action=3, 
		@on_success_step_id=0, 
		@on_fail_action=3, 
		@on_fail_step_id=0, 
		@retry_attempts=0, 
		@retry_interval=0, 
		@os_run_priority=0, @subsystem=N'TSQL', 
		@command=N'exec dbo.usp_sqlwatch_logger_performance', 
		@database_name=N'SQLWATCH', 
		@flags=0
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobstep @job_id=@jobId, @step_name=N'dbo.usp_sqlwatch_logger_requests_and_sessions', 
		@step_id=2, 
		@cmdexec_success_code=0, 
		@on_success_action=3, 
		@on_success_step_id=0, 
		@on_fail_action=3, 
		@on_fail_step_id=0, 
		@retry_attempts=0, 
		@retry_interval=0, 
		@os_run_priority=0, @subsystem=N'TSQL', 
		@command=N'exec dbo.usp_sqlwatch_logger_requests_and_sessions', 
		@database_name=N'SQLWATCH', 
		@flags=0
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobstep @job_id=@jobId, @step_name=N'dbo.usp_sqlwatch_logger_xes_blockers', 
		@step_id=3, 
		@cmdexec_success_code=0, 
		@on_success_action=1, 
		@on_success_step_id=0, 
		@on_fail_action=2, 
		@on_fail_step_id=0, 
		@retry_attempts=0, 
		@retry_interval=0, 
		@os_run_priority=0, @subsystem=N'TSQL', 
		@command=N'exec dbo.usp_sqlwatch_logger_xes_blockers', 
		@database_name=N'SQLWATCH', 
		@flags=0
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_update_job @job_id = @jobId, @start_step_id = 1
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobschedule @job_id=@jobId, @name=N'SQLWATCH-LOGGER-PERFORMANCE', 
		@enabled=1, 
		@freq_type=4, 
		@freq_interval=1, 
		@freq_subday_type=2, 
		@freq_subday_interval=10, 
		@freq_relative_interval=0, 
		@freq_recurrence_factor=0, 
		@active_start_date=20180101, 
		@active_end_date=99991231, 
		@active_start_time=12, 
		@active_end_time=235959, 
		@schedule_uid=N'8cebdd4b-1b21-48df-8616-211bab9a08f0'
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobserver @job_id = @jobId, @server_name = N'(local)'
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
COMMIT TRANSACTION
GOTO EndSave
QuitWithRollback:
    IF (@@TRANCOUNT > 0) ROLLBACK TRANSACTION
EndSave:
GO



BEGIN TRANSACTION
DECLARE @ReturnCode INT
SELECT @ReturnCode = 0
IF NOT EXISTS (SELECT name FROM msdb.dbo.syscategories WHERE name=N'Data Collector' AND category_class=1)
BEGIN
EXEC @ReturnCode = msdb.dbo.sp_add_category @class=N'JOB', @type=N'LOCAL', @name=N'Data Collector'
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback

END

DECLARE @jobId BINARY(16)
EXEC @ReturnCode =  msdb.dbo.sp_add_job @job_name=N'SQLWATCH-LOGGER-PROCS', 
		@enabled=1, 
		@notify_level_eventlog=2, 
		@notify_level_email=0, 
		@notify_level_netsend=0, 
		@notify_level_page=0, 
		@delete_level=0, 
		@description=N'https://sqlwatch.io', 
		@category_name=N'Data Collector', 
		@owner_login_name=N'sa', @job_id = @jobId OUTPUT
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobstep @job_id=@jobId, @step_name=N'dbo.usp_sqlwatch_logger_procedure_stats', 
		@step_id=1, 
		@cmdexec_success_code=0, 
		@on_success_action=1, 
		@on_success_step_id=0, 
		@on_fail_action=2, 
		@on_fail_step_id=0, 
		@retry_attempts=0, 
		@retry_interval=0, 
		@os_run_priority=0, @subsystem=N'TSQL', 
		@command=N'exec dbo.usp_sqlwatch_logger_procedure_stats', 
		@database_name=N'SQLWATCH', 
		@flags=0
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_update_job @job_id = @jobId, @start_step_id = 1
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobschedule @job_id=@jobId, @name=N'SQLWATCH-LOGGER-PROCS', 
		@enabled=1, 
		@freq_type=4, 
		@freq_interval=1, 
		@freq_subday_type=4, 
		@freq_subday_interval=10, 
		@freq_relative_interval=0, 
		@freq_recurrence_factor=0, 
		@active_start_date=20180101, 
		@active_end_date=99991231, 
		@active_start_time=30, 
		@active_end_time=235959, 
		@schedule_uid=N'37ce79f8-bfcf-4c53-9184-a9f184b4cf8a'
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobserver @job_id = @jobId, @server_name = N'(local)'
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
COMMIT TRANSACTION
GOTO EndSave
QuitWithRollback:
    IF (@@TRANCOUNT > 0) ROLLBACK TRANSACTION
EndSave:
GO



BEGIN TRANSACTION
DECLARE @ReturnCode INT
SELECT @ReturnCode = 0
IF NOT EXISTS (SELECT name FROM msdb.dbo.syscategories WHERE name=N'Data Collector' AND category_class=1)
BEGIN
EXEC @ReturnCode = msdb.dbo.sp_add_category @class=N'JOB', @type=N'LOCAL', @name=N'Data Collector'
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback

END

DECLARE @jobId BINARY(16)
EXEC @ReturnCode =  msdb.dbo.sp_add_job @job_name=N'SQLWATCH-LOGGER-SYSCONFIG', 
		@enabled=1, 
		@notify_level_eventlog=2, 
		@notify_level_email=0, 
		@notify_level_netsend=0, 
		@notify_level_page=0, 
		@delete_level=0, 
		@description=N'https://sqlwatch.io', 
		@category_name=N'Data Collector', 
		@owner_login_name=N'sa', @job_id = @jobId OUTPUT
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobstep @job_id=@jobId, @step_name=N'dbo.usp_sqlwatch_internal_add_system_configuration', 
		@step_id=1, 
		@cmdexec_success_code=0, 
		@on_success_action=3, 
		@on_success_step_id=0, 
		@on_fail_action=3, 
		@on_fail_step_id=0, 
		@retry_attempts=0, 
		@retry_interval=0, 
		@os_run_priority=0, @subsystem=N'TSQL', 
		@command=N'exec dbo.usp_sqlwatch_internal_add_system_configuration', 
		@database_name=N'SQLWATCH', 
		@flags=0
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobstep @job_id=@jobId, @step_name=N'dbo.usp_sqlwatch_logger_system_configuration', 
		@step_id=2, 
		@cmdexec_success_code=0, 
		@on_success_action=1, 
		@on_success_step_id=0, 
		@on_fail_action=2, 
		@on_fail_step_id=0, 
		@retry_attempts=0, 
		@retry_interval=0, 
		@os_run_priority=0, @subsystem=N'TSQL', 
		@command=N'exec dbo.usp_sqlwatch_logger_system_configuration', 
		@database_name=N'SQLWATCH', 
		@flags=0
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_update_job @job_id = @jobId, @start_step_id = 1
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobschedule @job_id=@jobId, @name=N'SQLWATCH-LOGGER-SYSCONFIG', 
		@enabled=1, 
		@freq_type=4, 
		@freq_interval=1, 
		@freq_subday_type=1, 
		@freq_subday_interval=1, 
		@freq_relative_interval=0, 
		@freq_recurrence_factor=0, 
		@active_start_date=20180101, 
		@active_end_date=99991231, 
		@active_start_time=0, 
		@active_end_time=235959, 
		@schedule_uid=N'7688e7aa-30b1-401e-8f92-ddc4faa493d8'
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobserver @job_id = @jobId, @server_name = N'(local)'
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
COMMIT TRANSACTION
GOTO EndSave
QuitWithRollback:
    IF (@@TRANCOUNT > 0) ROLLBACK TRANSACTION
EndSave:
GO



BEGIN TRANSACTION
DECLARE @ReturnCode INT
SELECT @ReturnCode = 0
IF NOT EXISTS (SELECT name FROM msdb.dbo.syscategories WHERE name=N'Data Collector' AND category_class=1)
BEGIN
EXEC @ReturnCode = msdb.dbo.sp_add_category @class=N'JOB', @type=N'LOCAL', @name=N'Data Collector'
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback

END

DECLARE @jobId BINARY(16)
EXEC @ReturnCode =  msdb.dbo.sp_add_job @job_name=N'SQLWATCH-LOGGER-WHOISACTIVE', 
		@enabled=0, 
		@notify_level_eventlog=2, 
		@notify_level_email=0, 
		@notify_level_netsend=0, 
		@notify_level_page=0, 
		@delete_level=0, 
		@description=N'https://sqlwatch.io', 
		@category_name=N'Data Collector', 
		@owner_login_name=N'sa', @job_id = @jobId OUTPUT
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobstep @job_id=@jobId, @step_name=N'dbo.usp_sqlwatch_logger_whoisactive', 
		@step_id=1, 
		@cmdexec_success_code=0, 
		@on_success_action=1, 
		@on_success_step_id=0, 
		@on_fail_action=2, 
		@on_fail_step_id=0, 
		@retry_attempts=0, 
		@retry_interval=0, 
		@os_run_priority=0, @subsystem=N'TSQL', 
		@command=N'exec dbo.usp_sqlwatch_logger_whoisactive', 
		@database_name=N'SQLWATCH', 
		@flags=0
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_update_job @job_id = @jobId, @start_step_id = 1
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobschedule @job_id=@jobId, @name=N'SQLWATCH-LOGGER-WHOISACTIVE', 
		@enabled=1, 
		@freq_type=4, 
		@freq_interval=1, 
		@freq_subday_type=2, 
		@freq_subday_interval=15, 
		@freq_relative_interval=0, 
		@freq_recurrence_factor=0, 
		@active_start_date=20180101, 
		@active_end_date=99991231, 
		@active_start_time=0, 
		@active_end_time=235959, 
		@schedule_uid=N'95331594-7ce4-4af4-943f-e5c700021b66'
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobserver @job_id = @jobId, @server_name = N'(local)'
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
COMMIT TRANSACTION
GOTO EndSave
QuitWithRollback:
    IF (@@TRANCOUNT > 0) ROLLBACK TRANSACTION
EndSave:
GO



BEGIN TRANSACTION
DECLARE @ReturnCode INT
SELECT @ReturnCode = 0
IF NOT EXISTS (SELECT name FROM msdb.dbo.syscategories WHERE name=N'Data Collector' AND category_class=1)
BEGIN
EXEC @ReturnCode = msdb.dbo.sp_add_category @class=N'JOB', @type=N'LOCAL', @name=N'Data Collector'
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback

END

DECLARE @jobId BINARY(16)
EXEC @ReturnCode =  msdb.dbo.sp_add_job @job_name=N'SQLWATCH-LOGGER-XES', 
		@enabled=1, 
		@notify_level_eventlog=2, 
		@notify_level_email=0, 
		@notify_level_netsend=0, 
		@notify_level_page=0, 
		@delete_level=0, 
		@description=N'https://sqlwatch.io', 
		@category_name=N'Data Collector', 
		@owner_login_name=N'sa', @job_id = @jobId OUTPUT
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobstep @job_id=@jobId, @step_name=N'dbo.usp_sqlwatch_logger_xes_waits', 
		@step_id=1, 
		@cmdexec_success_code=0, 
		@on_success_action=3, 
		@on_success_step_id=0, 
		@on_fail_action=3, 
		@on_fail_step_id=0, 
		@retry_attempts=0, 
		@retry_interval=0, 
		@os_run_priority=0, @subsystem=N'TSQL', 
		@command=N'exec dbo.usp_sqlwatch_logger_xes_waits', 
		@database_name=N'SQLWATCH', 
		@flags=0
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobstep @job_id=@jobId, @step_name=N'dbo.usp_sqlwatch_logger_xes_diagnostics', 
		@step_id=2, 
		@cmdexec_success_code=0, 
		@on_success_action=3, 
		@on_success_step_id=0, 
		@on_fail_action=3, 
		@on_fail_step_id=0, 
		@retry_attempts=0, 
		@retry_interval=0, 
		@os_run_priority=0, @subsystem=N'TSQL', 
		@command=N'exec dbo.usp_sqlwatch_logger_xes_diagnostics', 
		@database_name=N'SQLWATCH', 
		@flags=0
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobstep @job_id=@jobId, @step_name=N'dbo.usp_sqlwatch_logger_xes_long_queries', 
		@step_id=3, 
		@cmdexec_success_code=0, 
		@on_success_action=1, 
		@on_success_step_id=0, 
		@on_fail_action=2, 
		@on_fail_step_id=0, 
		@retry_attempts=0, 
		@retry_interval=0, 
		@os_run_priority=0, @subsystem=N'TSQL', 
		@command=N'exec dbo.usp_sqlwatch_logger_xes_long_queries', 
		@database_name=N'SQLWATCH', 
		@flags=0
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_update_job @job_id = @jobId, @start_step_id = 1
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobschedule @job_id=@jobId, @name=N'SQLWATCH-LOGGER-XES', 
		@enabled=1, 
		@freq_type=4, 
		@freq_interval=1, 
		@freq_subday_type=4, 
		@freq_subday_interval=1, 
		@freq_relative_interval=0, 
		@freq_recurrence_factor=0, 
		@active_start_date=20180101, 
		@active_end_date=99991231, 
		@active_start_time=12, 
		@active_end_time=235959, 
		@schedule_uid=N'9002ba3f-4dd5-4cd6-a3aa-67fc35ed1ca7'
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobserver @job_id = @jobId, @server_name = N'(local)'
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
COMMIT TRANSACTION
GOTO EndSave
QuitWithRollback:
    IF (@@TRANCOUNT > 0) ROLLBACK TRANSACTION
EndSave:
GO




BEGIN TRANSACTION
DECLARE @ReturnCode INT
SELECT @ReturnCode = 0
IF NOT EXISTS (SELECT name FROM msdb.dbo.syscategories WHERE name=N'Data Collector' AND category_class=1)
BEGIN
EXEC @ReturnCode = msdb.dbo.sp_add_category @class=N'JOB', @type=N'LOCAL', @name=N'Data Collector'
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback

END

DECLARE @jobId BINARY(16)
EXEC @ReturnCode =  msdb.dbo.sp_add_job @job_name=N'SQLWATCH-REPORT-AZMONITOR', 
		@enabled=1, 
		@notify_level_eventlog=2, 
		@notify_level_email=0, 
		@notify_level_netsend=0, 
		@notify_level_page=0, 
		@delete_level=0, 
		@description=N'https://sqlwatch.io', 
		@category_name=N'Data Collector', 
		@owner_login_name=N'sa', @job_id = @jobId OUTPUT
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobstep @job_id=@jobId, @step_name=N'dbo.usp_sqlwatch_internal_process_reports', 
		@step_id=1, 
		@cmdexec_success_code=0, 
		@on_success_action=1, 
		@on_success_step_id=0, 
		@on_fail_action=2, 
		@on_fail_step_id=0, 
		@retry_attempts=0, 
		@retry_interval=0, 
		@os_run_priority=0, @subsystem=N'TSQL', 
		@command=N'exec dbo.usp_sqlwatch_internal_process_reports @report_batch_id = ''AzureLogMonitor-1''', 
		@database_name=N'SQLWATCH', 
		@flags=0
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_update_job @job_id = @jobId, @start_step_id = 1
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobschedule @job_id=@jobId, @name=N'SQLWATCH-REPORT-AZMONITOR', 
		@enabled=1, 
		@freq_type=4, 
		@freq_interval=1, 
		@freq_subday_type=4, 
		@freq_subday_interval=10, 
		@freq_relative_interval=0, 
		@freq_recurrence_factor=0, 
		@active_start_date=20180101, 
		@active_end_date=99991231, 
		@active_start_time=21, 
		@active_end_time=235959, 
		@schedule_uid=N'83e716fa-0228-43c8-8ab0-9b7652ba2bd4'
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
EXEC @ReturnCode = msdb.dbo.sp_add_jobserver @job_id = @jobId, @server_name = N'(local)'
IF (@@ERROR <> 0 OR @ReturnCode <> 0) GOTO QuitWithRollback
COMMIT TRANSACTION
GOTO EndSave
QuitWithRollback:
    IF (@@TRANCOUNT > 0) ROLLBACK TRANSACTION
EndSave:
GO



BEGIN TRY

	CREATE EVENT SESSION [SQLWATCH_blockers] ON SERVER 
	ADD EVENT sqlserver.blocked_process_report(
		ACTION(sqlserver.client_app_name,sqlserver.client_hostname,sqlserver.database_name,sqlserver.plan_handle,sqlserver.session_id,sqlserver.session_nt_username,sqlserver.sql_text,sqlserver.tsql_stack,sqlserver.username)),
	ADD EVENT sqlserver.xml_deadlock_report
	ADD TARGET package0.event_file(SET filename=N'SQLWATCH_blockers.xel',max_file_size=(5),max_rollover_files=(0))
	WITH (MAX_MEMORY=4096 KB,EVENT_RETENTION_MODE=ALLOW_SINGLE_EVENT_LOSS,MAX_DISPATCH_LATENCY=1 SECONDS,MAX_EVENT_SIZE=0 KB,MEMORY_PARTITION_MODE=NONE,TRACK_CAUSALITY=ON,STARTUP_STATE=ON)
END TRY
BEGIN CATCH
END CATCH

BEGIN TRY
CREATE EVENT SESSION [SQLWATCH_health] ON SERVER 
ADD EVENT sqlserver.error_reported(
    ACTION(sqlserver.sql_text)
    WHERE ([sqlserver].[like_i_sql_unicode_string]([sqlserver].[sql_text],N'%sqlwatch%') AND NOT [sqlserver].[like_i_sql_unicode_string]([message],N'%Warning:%'))),
ADD EVENT sqlserver.sp_statement_completed(SET collect_statement=(1)
    ACTION(sqlserver.sql_text)
    WHERE ([package0].[greater_than_int64]([duration],(1000000)) AND [sqlserver].[like_i_sql_unicode_string]([sqlserver].[sql_text],N'%usp_sqlwatch%') AND NOT [sqlserver].[like_i_sql_unicode_string]([sqlserver].[sql_text],N'%usp_sqlwatch_logger_whoisactive%') OR [package0].[greater_than_int64]([duration],(500000)) AND [sqlserver].[like_i_sql_unicode_string]([sqlserver].[sql_text],N'%usp_sqlwatch_logger_performance%')))
ADD TARGET package0.event_file(SET filename=N'SQLWATCH_internal_performance',max_file_size=(50),max_rollover_files=(1))
WITH (MAX_MEMORY=4096 KB,EVENT_RETENTION_MODE=ALLOW_SINGLE_EVENT_LOSS,MAX_DISPATCH_LATENCY=30 SECONDS,MAX_EVENT_SIZE=0 KB,MEMORY_PARTITION_MODE=NONE,TRACK_CAUSALITY=OFF,STARTUP_STATE=OFF)
END TRY
BEGIN CATCH
	PRINT 'Error with event [SQLWATCH_health]'
END CATCH

BEGIN TRY

CREATE EVENT SESSION [SQLWATCH_long_queries] ON SERVER 
ADD EVENT sqlserver.module_end(SET collect_statement=(0)
    ACTION(sqlserver.client_app_name,sqlserver.client_hostname,sqlserver.database_name,sqlserver.plan_handle,sqlserver.session_id,sqlserver.session_nt_username,sqlserver.sql_text,sqlserver.tsql_stack,sqlserver.username)
    WHERE ([package0].[greater_than_uint64]([duration],(5000000)) AND [sqlserver].[is_system]=(0) AND [sqlserver].[client_app_name]<>N'Microsoft SQL Server Management Studio' AND [sqlserver].[client_app_name]<>N'Microsoft SQL Server Management Studio - Transact-SQL IntelliSense' AND [sqlserver].[client_app_name]<>N'SQLServerCEIP' AND [sqlserver].[client_app_name]<>N'SQLAgent - Job Manager' AND [sqlserver].[client_app_name]<>N'SQLAgent - Job invocation engine' AND [sqlserver].[client_app_name]<>N'SQLAgent - Schedule Saver')),
ADD EVENT sqlserver.rpc_completed(SET collect_statement=(0)
    ACTION(sqlserver.client_app_name,sqlserver.client_hostname,sqlserver.database_name,sqlserver.plan_handle,sqlserver.session_id,sqlserver.session_nt_username,sqlserver.sql_text,sqlserver.tsql_stack,sqlserver.username)
    WHERE ([package0].[greater_than_uint64]([duration],(5000000)) AND [package0].[greater_than_uint64]([cpu_time],(0)) AND ([result]=(2) OR [package0].[greater_than_uint64]([logical_reads],(0)) OR [package0].[greater_than_uint64]([physical_reads],(0)) OR [package0].[greater_than_uint64]([writes],(0))) AND [package0].[equal_boolean]([sqlserver].[is_system],(0)) AND [sqlserver].[client_app_name]<>N'Microsoft SQL Server Management Studio' AND [sqlserver].[client_app_name]<>N'Microsoft SQL Server Management Studio - Transact-SQL IntelliSense' AND [sqlserver].[client_app_name]<>N'SQLServerCEIP' AND [sqlserver].[client_app_name]<>N'SQLAgent - Job Manager' AND [sqlserver].[client_app_name]<>N'SQLAgent - Job invocation engine' AND [sqlserver].[client_app_name]<>N'SQLAgent - Schedule Saver')),
ADD EVENT sqlserver.sp_statement_completed(SET collect_statement=(0)
    ACTION(sqlserver.client_app_name,sqlserver.client_hostname,sqlserver.database_name,sqlserver.plan_handle,sqlserver.session_id,sqlserver.session_nt_username,sqlserver.sql_text,sqlserver.tsql_stack,sqlserver.username)
    WHERE ([package0].[greater_than_int64]([duration],(5000000)) AND [package0].[greater_than_uint64]([cpu_time],(0)) AND ([package0].[greater_than_uint64]([logical_reads],(0)) OR [package0].[greater_than_uint64]([physical_reads],(0)) OR [package0].[greater_than_uint64]([writes],(0))) AND [sqlserver].[is_system]=(0) AND [sqlserver].[client_app_name]<>N'Microsoft SQL Server Management Studio' AND [sqlserver].[client_app_name]<>N'Microsoft SQL Server Management Studio - Transact-SQL IntelliSense' AND [sqlserver].[client_app_name]<>N'SQLServerCEIP' AND [sqlserver].[client_app_name]<>N'SQLAgent - Job Manager' AND [sqlserver].[client_app_name]<>N'SQLAgent - Job invocation engine' AND [sqlserver].[client_app_name]<>N'SQLAgent - Schedule Saver')),
ADD EVENT sqlserver.sql_statement_completed(SET collect_statement=(0)
    ACTION(sqlserver.client_app_name,sqlserver.client_hostname,sqlserver.database_name,sqlserver.plan_handle,sqlserver.session_id,sqlserver.session_nt_username,sqlserver.sql_text,sqlserver.tsql_stack,sqlserver.username)
    WHERE ([package0].[greater_than_int64]([duration],(5000000)) AND [package0].[greater_than_uint64]([cpu_time],(0)) AND ([package0].[greater_than_uint64]([logical_reads],(0)) OR [package0].[greater_than_uint64]([physical_reads],(0)) OR [package0].[greater_than_uint64]([writes],(0))) AND [sqlserver].[is_system]=(0) AND [sqlserver].[client_app_name]<>N'Microsoft SQL Server Management Studio' AND [sqlserver].[client_app_name]<>N'Microsoft SQL Server Management Studio - Transact-SQL IntelliSense' AND [sqlserver].[client_app_name]<>N'SQLServerCEIP' AND [sqlserver].[client_app_name]<>N'SQLAgent - Job Manager' AND [sqlserver].[client_app_name]<>N'SQLAgent - Job invocation engine' AND [sqlserver].[client_app_name]<>N'SQLAgent - Schedule Saver'))
ADD TARGET package0.event_file(SET filename=N'SQLWATCH_long_queries.xel',max_file_size=(5),max_rollover_files=(0))
WITH (MAX_MEMORY=4096 KB,EVENT_RETENTION_MODE=ALLOW_SINGLE_EVENT_LOSS,MAX_DISPATCH_LATENCY=5 SECONDS,MAX_EVENT_SIZE=0 KB,MEMORY_PARTITION_MODE=NONE,TRACK_CAUSALITY=ON,STARTUP_STATE=ON)
END TRY
BEGIN CATCH
	PRINT 'Error with event [SQLWATCH_long_queries]'
END CATCH

BEGIN TRY

CREATE EVENT SESSION [SQLWATCH_query_problems] ON SERVER 
ADD EVENT sqlserver.additional_memory_grant(
    ACTION(sqlserver.client_app_name,sqlserver.client_hostname,sqlserver.database_name,sqlserver.nt_username,sqlserver.plan_handle,sqlserver.sql_text,sqlserver.username)
    WHERE ([sqlserver].[is_system]=(0) AND [sqlserver].[client_app_name]<>N'Microsoft SQL Server Management Studio' AND [sqlserver].[client_app_name]<>N'Microsoft SQL Server Management Studio - Transact-SQL IntelliSense' AND [sqlserver].[client_app_name]<>N'SQLServerCEIP' AND [sqlserver].[client_app_name]<>N'DacFx Deploy' AND NOT [sqlserver].[like_i_sql_unicode_string]([sqlserver].[sql_text],N'%whoisactive%') AND NOT [sqlserver].[like_i_sql_unicode_string]([sqlserver].[sql_text],N'%WhoIsActive%'))),
ADD EVENT sqlserver.exchange_spill(
    ACTION(sqlserver.client_app_name,sqlserver.client_hostname,sqlserver.database_name,sqlserver.nt_username,sqlserver.plan_handle,sqlserver.sql_text,sqlserver.username)
    WHERE ([sqlserver].[is_system]=(0) AND [sqlserver].[client_app_name]<>N'Microsoft SQL Server Management Studio' AND [sqlserver].[client_app_name]<>N'Microsoft SQL Server Management Studio - Transact-SQL IntelliSense' AND [sqlserver].[client_app_name]<>N'SQLServerCEIP' AND [sqlserver].[client_app_name]<>N'DacFx Deploy' AND NOT [sqlserver].[like_i_sql_unicode_string]([sqlserver].[sql_text],N'%whoisactive%') AND NOT [sqlserver].[like_i_sql_unicode_string]([sqlserver].[sql_text],N'%WhoIsActive%'))),
ADD EVENT sqlserver.execution_warning(
    ACTION(sqlserver.client_app_name,sqlserver.client_hostname,sqlserver.database_name,sqlserver.nt_username,sqlserver.plan_handle,sqlserver.sql_text,sqlserver.username)
    WHERE ([sqlserver].[is_system]=(0) AND [sqlserver].[client_app_name]<>N'Microsoft SQL Server Management Studio' AND [sqlserver].[client_app_name]<>N'Microsoft SQL Server Management Studio - Transact-SQL IntelliSense' AND [sqlserver].[client_app_name]<>N'SQLServerCEIP' AND [sqlserver].[client_app_name]<>N'DacFx Deploy' AND NOT [sqlserver].[like_i_sql_unicode_string]([sqlserver].[sql_text],N'%whoisactive%') AND NOT [sqlserver].[like_i_sql_unicode_string]([sqlserver].[sql_text],N'%WhoIsActive%'))),
ADD EVENT sqlserver.hash_warning(
    ACTION(sqlserver.client_app_name,sqlserver.client_hostname,sqlserver.database_name,sqlserver.nt_username,sqlserver.plan_handle,sqlserver.sql_text,sqlserver.username)
    WHERE ([sqlserver].[is_system]=(0) AND [sqlserver].[client_app_name]<>N'Microsoft SQL Server Management Studio' AND [sqlserver].[client_app_name]<>N'Microsoft SQL Server Management Studio - Transact-SQL IntelliSense' AND [sqlserver].[client_app_name]<>N'SQLServerCEIP' AND [sqlserver].[client_app_name]<>N'DacFx Deploy' AND NOT [sqlserver].[like_i_sql_unicode_string]([sqlserver].[sql_text],N'%whoisactive%') AND NOT [sqlserver].[like_i_sql_unicode_string]([sqlserver].[sql_text],N'%WhoIsActive%'))),
ADD EVENT sqlserver.long_io_detected(
    ACTION(sqlserver.client_app_name,sqlserver.client_hostname,sqlserver.database_name,sqlserver.nt_username,sqlserver.plan_handle,sqlserver.sql_text,sqlserver.username)
    WHERE ([sqlserver].[is_system]=(0) AND [sqlserver].[client_app_name]<>N'Microsoft SQL Server Management Studio' AND [sqlserver].[client_app_name]<>N'Microsoft SQL Server Management Studio - Transact-SQL IntelliSense' AND [sqlserver].[client_app_name]<>N'SQLServerCEIP' AND [sqlserver].[client_app_name]<>N'DacFx Deploy' AND NOT [sqlserver].[like_i_sql_unicode_string]([sqlserver].[sql_text],N'%whoisactive%') AND NOT [sqlserver].[like_i_sql_unicode_string]([sqlserver].[sql_text],N'%WhoIsActive%'))),
ADD EVENT sqlserver.missing_column_statistics(SET collect_column_list=(1)
    ACTION(sqlserver.client_app_name,sqlserver.client_hostname,sqlserver.database_name,sqlserver.nt_username,sqlserver.plan_handle,sqlserver.sql_text,sqlserver.username)
    WHERE ([sqlserver].[is_system]=(0) AND [sqlserver].[client_app_name]<>N'Microsoft SQL Server Management Studio' AND [sqlserver].[client_app_name]<>N'Microsoft SQL Server Management Studio - Transact-SQL IntelliSense' AND [sqlserver].[client_app_name]<>N'SQLServerCEIP' AND [sqlserver].[client_app_name]<>N'DacFx Deploy' AND NOT [sqlserver].[like_i_sql_unicode_string]([sqlserver].[sql_text],N'%whoisactive%') AND NOT [sqlserver].[like_i_sql_unicode_string]([sqlserver].[sql_text],N'%WhoIsActive%'))),
ADD EVENT sqlserver.missing_join_predicate(
    ACTION(sqlserver.client_app_name,sqlserver.client_hostname,sqlserver.database_name,sqlserver.nt_username,sqlserver.plan_handle,sqlserver.sql_text,sqlserver.username)
    WHERE ([sqlserver].[is_system]=(0) AND [sqlserver].[client_app_name]<>N'Microsoft SQL Server Management Studio' AND [sqlserver].[client_app_name]<>N'SQLServerCEIP' AND [sqlserver].[client_app_name]<>N'DacFx Deploy' AND NOT [sqlserver].[like_i_sql_unicode_string]([sqlserver].[sql_text],N'%whoisactive%') AND NOT [sqlserver].[like_i_sql_unicode_string]([sqlserver].[sql_text],N'%WhoIsActive%'))),
ADD EVENT sqlserver.optimizer_timeout(
    ACTION(sqlserver.client_app_name,sqlserver.client_hostname,sqlserver.database_name,sqlserver.nt_username,sqlserver.plan_handle,sqlserver.sql_text,sqlserver.username)
    WHERE ([sqlserver].[is_system]=(0) AND [sqlserver].[client_app_name]<>N'Microsoft SQL Server Management Studio' AND [sqlserver].[client_app_name]<>N'Microsoft SQL Server Management Studio - Transact-SQL IntelliSense' AND [sqlserver].[client_app_name]<>N'SQLServerCEIP' AND [sqlserver].[client_app_name]<>N'DacFx Deploy' AND NOT [sqlserver].[like_i_sql_unicode_string]([sqlserver].[sql_text],N'%whoisactive%') AND NOT [sqlserver].[like_i_sql_unicode_string]([sqlserver].[sql_text],N'%WhoIsActive%'))),
ADD EVENT sqlserver.page_split(
    ACTION(sqlserver.client_app_name,sqlserver.client_hostname,sqlserver.database_name,sqlserver.nt_username,sqlserver.plan_handle,sqlserver.sql_text,sqlserver.username)
    WHERE ([sqlserver].[is_system]=(0) AND [sqlserver].[client_app_name]<>N'Microsoft SQL Server Management Studio' AND [sqlserver].[client_app_name]<>N'Microsoft SQL Server Management Studio - Transact-SQL IntelliSense' AND [sqlserver].[client_app_name]<>N'SQLServerCEIP' AND [sqlserver].[client_app_name]<>N'DacFx Deploy' AND [sqlserver].[database_name]<>N'master' AND [sqlserver].[database_name]<>N'msdb' AND NOT [sqlserver].[like_i_sql_unicode_string]([sqlserver].[sql_text],N'%whoisactive%') AND NOT [sqlserver].[like_i_sql_unicode_string]([sqlserver].[sql_text],N'%WhoIsActive%'))),
ADD EVENT sqlserver.plan_affecting_convert(
    ACTION(sqlserver.client_app_name,sqlserver.client_hostname,sqlserver.database_name,sqlserver.nt_username,sqlserver.plan_handle,sqlserver.sql_text,sqlserver.username)
    WHERE ([sqlserver].[is_system]=(0) AND [sqlserver].[client_app_name]<>N'Microsoft SQL Server Management Studio' AND [sqlserver].[client_app_name]<>N'Microsoft SQL Server Management Studio - Transact-SQL IntelliSense' AND [sqlserver].[client_app_name]<>N'SQLServerCEIP' AND [sqlserver].[client_app_name]<>N'DacFx Deploy' AND [sqlserver].[database_name]<>N'master' AND [sqlserver].[database_name]<>N'msdb' AND NOT [sqlserver].[like_i_sql_unicode_string]([sqlserver].[sql_text],N'%whoisactive%') AND NOT [sqlserver].[like_i_sql_unicode_string]([sqlserver].[sql_text],N'%WhoIsActive%'))),
ADD EVENT sqlserver.sort_warning(
    ACTION(sqlserver.client_app_name,sqlserver.client_hostname,sqlserver.database_name,sqlserver.nt_username,sqlserver.plan_handle,sqlserver.sql_text,sqlserver.username)
    WHERE ([sqlserver].[is_system]=(0) AND [sqlserver].[client_app_name]<>N'Microsoft SQL Server Management Studio' AND [sqlserver].[client_app_name]<>N'Microsoft SQL Server Management Studio - Transact-SQL IntelliSense' AND [sqlserver].[client_app_name]<>N'SQLServerCEIP' AND [sqlserver].[client_app_name]<>N'DacFx Deploy' AND NOT [sqlserver].[like_i_sql_unicode_string]([sqlserver].[sql_text],N'%whoisactive%') AND NOT [sqlserver].[like_i_sql_unicode_string]([sqlserver].[sql_text],N'%WhoIsActive%'))),
ADD EVENT sqlserver.sp_cache_miss(
    ACTION(sqlserver.client_app_name,sqlserver.client_hostname,sqlserver.nt_username,sqlserver.plan_handle,sqlserver.sql_text,sqlserver.username)
    WHERE ([sqlserver].[is_system]=(0) AND [sqlserver].[client_app_name]<>N'Microsoft SQL Server Management Studio' AND [sqlserver].[client_app_name]<>N'Microsoft SQL Server Management Studio - Transact-SQL IntelliSense' AND [sqlserver].[client_app_name]<>N'SQLServerCEIP' AND [sqlserver].[client_app_name]<>N'DacFx Deploy' AND NOT [sqlserver].[like_i_sql_unicode_string]([sqlserver].[sql_text],N'%whoisactive%') AND NOT [sqlserver].[like_i_sql_unicode_string]([sqlserver].[sql_text],N'%WhoIsActive%'))),
ADD EVENT sqlserver.unmatched_filtered_indexes(
    ACTION(sqlserver.client_app_name,sqlserver.client_hostname,sqlserver.database_name,sqlserver.nt_username,sqlserver.plan_handle,sqlserver.sql_text,sqlserver.username)
    WHERE ([sqlserver].[is_system]=(0) AND [sqlserver].[client_app_name]<>N'Microsoft SQL Server Management Studio' AND [sqlserver].[client_app_name]<>N'Microsoft SQL Server Management Studio - Transact-SQL IntelliSense' AND [sqlserver].[client_app_name]<>N'SQLServerCEIP' AND [sqlserver].[client_app_name]<>N'DacFx Deploy' AND NOT [sqlserver].[like_i_sql_unicode_string]([sqlserver].[sql_text],N'%whoisactive%') AND NOT [sqlserver].[like_i_sql_unicode_string]([sqlserver].[sql_text],N'%WhoIsActive%')))
ADD TARGET package0.event_file(SET filename=N'SQLWATCH_query_problems.xel',max_file_size=(5),max_rollover_files=(0))
WITH (MAX_MEMORY=4096 KB,EVENT_RETENTION_MODE=ALLOW_SINGLE_EVENT_LOSS,MAX_DISPATCH_LATENCY=30 SECONDS,MAX_EVENT_SIZE=0 KB,MEMORY_PARTITION_MODE=NONE,TRACK_CAUSALITY=OFF,STARTUP_STATE=ON)
END TRY
BEGIN CATCH
	PRINT 'Error with event [SQLWATCH_query_problems]'
END CATCH

BEGIN TRY

CREATE EVENT SESSION [SQLWATCH_waits] ON SERVER 
ADD EVENT sqlos.wait_info(
    ACTION(sqlserver.client_app_name,sqlserver.client_hostname,sqlserver.database_name,sqlserver.plan_handle,sqlserver.session_id,sqlserver.session_nt_username,sqlserver.sql_text,sqlserver.tsql_frame,sqlserver.username)
    WHERE ([package0].[greater_than_uint64]([duration],(1000)) AND [package0].[greater_than_uint64]([sqlserver].[database_id],(0)) AND [package0].[equal_boolean]([sqlserver].[is_system],(0)) AND [sqlserver].[client_app_name]<>N'Microsoft SQL Server Management Studio' AND [sqlserver].[client_app_name]<>N'Microsoft SQL Server Management Studio - Transact-SQL IntelliSense' AND [sqlserver].[client_app_name]<>N'SQLAgent - Job Manager' AND [sqlserver].[client_app_name]<>N'SQLAgent - Job invocation engine' AND [sqlserver].[client_app_name]<>N'SQLAgent - Schedule Saver')),
ADD EVENT sqlos.wait_info_external(
    ACTION(sqlserver.client_app_name,sqlserver.client_hostname,sqlserver.database_name,sqlserver.plan_handle,sqlserver.session_id,sqlserver.session_nt_username,sqlserver.sql_text,sqlserver.tsql_frame,sqlserver.username)
    WHERE ([package0].[greater_than_uint64]([duration],(1000)) AND [package0].[greater_than_uint64]([sqlserver].[database_id],(0)) AND [package0].[equal_boolean]([sqlserver].[is_system],(0)) AND [sqlserver].[client_app_name]<>N'Microsoft SQL Server Management Studio' AND [sqlserver].[client_app_name]<>N'Microsoft SQL Server Management Studio - Transact-SQL IntelliSense' AND [sqlserver].[client_app_name]<>N'SQLAgent - Job Manager' AND [sqlserver].[client_app_name]<>N'SQLAgent - Job invocation engine' AND [sqlserver].[client_app_name]<>N'SQLAgent - Schedule Saver'))
ADD TARGET package0.event_file(SET filename=N'SQLWATCH_waits.xel',max_file_size=(2),max_rollover_files=(1))
WITH (MAX_MEMORY=4096 KB,EVENT_RETENTION_MODE=ALLOW_SINGLE_EVENT_LOSS,MAX_DISPATCH_LATENCY=1 SECONDS,MAX_EVENT_SIZE=0 KB,MEMORY_PARTITION_MODE=NONE,TRACK_CAUSALITY=ON,STARTUP_STATE=ON)
END TRY
BEGIN CATCH
	PRINT 'Error with event [SQLWATCH_waits]'
END CATCH



