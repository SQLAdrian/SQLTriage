USE [master]
GO

CREATE DATABASE [SQLWATCH]
GO

GO
USE [SQLWATCH]
GO
IF (1 = FULLTEXTSERVICEPROPERTY('IsFullTextInstalled'))
begin
EXEC [SQLWATCH].[dbo].[sp_fulltext_database] @action = 'disable'
end
GO
ALTER DATABASE [SQLWATCH] SET ANSI_NULL_DEFAULT ON 
GO
ALTER DATABASE [SQLWATCH] SET ANSI_NULLS ON 
GO
ALTER DATABASE [SQLWATCH] SET ANSI_PADDING ON 
GO
ALTER DATABASE [SQLWATCH] SET ANSI_WARNINGS ON 
GO
ALTER DATABASE [SQLWATCH] SET ARITHABORT ON 
GO
ALTER DATABASE [SQLWATCH] SET AUTO_CLOSE OFF 
GO
ALTER DATABASE [SQLWATCH] SET AUTO_SHRINK OFF 
GO
ALTER DATABASE [SQLWATCH] SET AUTO_UPDATE_STATISTICS ON 
GO
ALTER DATABASE [SQLWATCH] SET CURSOR_CLOSE_ON_COMMIT OFF 
GO
ALTER DATABASE [SQLWATCH] SET CURSOR_DEFAULT  LOCAL 
GO
ALTER DATABASE [SQLWATCH] SET CONCAT_NULL_YIELDS_NULL ON 
GO
ALTER DATABASE [SQLWATCH] SET NUMERIC_ROUNDABORT OFF 
GO
ALTER DATABASE [SQLWATCH] SET QUOTED_IDENTIFIER ON 
GO
ALTER DATABASE [SQLWATCH] SET RECURSIVE_TRIGGERS OFF 
GO
ALTER DATABASE [SQLWATCH] SET  DISABLE_BROKER 
GO
ALTER DATABASE [SQLWATCH] SET AUTO_UPDATE_STATISTICS_ASYNC OFF 
GO
ALTER DATABASE [SQLWATCH] SET DATE_CORRELATION_OPTIMIZATION OFF 
GO
ALTER DATABASE [SQLWATCH] SET TRUSTWORTHY ON
GO
ALTER DATABASE [SQLWATCH] SET ALLOW_SNAPSHOT_ISOLATION ON 
GO
ALTER DATABASE [SQLWATCH] SET PARAMETERIZATION SIMPLE 
GO
ALTER DATABASE [SQLWATCH] SET READ_COMMITTED_SNAPSHOT ON 
GO
ALTER DATABASE [SQLWATCH] SET HONOR_BROKER_PRIORITY OFF 
GO
ALTER DATABASE [SQLWATCH] SET RECOVERY SIMPLE 
GO
ALTER DATABASE [SQLWATCH] SET  MULTI_USER 
GO
ALTER DATABASE [SQLWATCH] SET PAGE_VERIFY CHECKSUM  
GO
ALTER DATABASE [SQLWATCH] SET DB_CHAINING OFF 
GO
ALTER DATABASE [SQLWATCH] SET FILESTREAM( NON_TRANSACTED_ACCESS = OFF ) 
GO
ALTER DATABASE [SQLWATCH] SET TARGET_RECOVERY_TIME = 0 SECONDS 
GO
ALTER DATABASE [SQLWATCH] SET DELAYED_DURABILITY = DISABLED  
GO
EXEC sys.sp_db_vardecimal_storage_format N'SQLWATCH', N'ON'
GO
ALTER DATABASE [SQLWATCH] SET QUERY_STORE = ON
GO
ALTER DATABASE [SQLWATCH] SET QUERY_STORE (OPERATION_MODE = READ_WRITE, CLEANUP_POLICY = (STALE_QUERY_THRESHOLD_DAYS = 30), DATA_FLUSH_INTERVAL_SECONDS = 900, INTERVAL_LENGTH_MINUTES = 60, MAX_STORAGE_SIZE_MB = 1000, QUERY_CAPTURE_MODE = AUTO, SIZE_BASED_CLEANUP_MODE = AUTO, MAX_PLANS_PER_QUERY = 200, WAIT_STATS_CAPTURE_MODE = ON)
GO
USE [SQLWATCH]
GO

CREATE ASSEMBLY [SqlWatchDatabase]
FROM 0x4D5A90000300000004000000FFFF0000B800000000000000400000000000000000000000000000000000000000000000000000000000000000000000800000000E1FBA0E00B409CD21B8014CCD21546869732070726F6772616D2063616E6E6F742062652072756E20696E20444F53206D6F64652E0D0D0A2400000000000000504500004C0103000C84D6680000000000000000E00022200B013000000600000006000000000000AA250000002000000040000000000010002000000002000004000000000000000600000000000000008000000002000000000000030060850000100000100000000010000010000000000000100000000000000000000000582500004F00000000400000BC03000000000000000000000000000000000000006000000C00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000200000080000000000000000000000082000004800000000000000000000002E74657874000000B0050000002000000006000000020000000000000000000000000000200000602E72737263000000BC030000004000000004000000080000000000000000000000000000400000402E72656C6F6300000C0000000060000000020000000C000000000000000000000000000040000042000000000000000000000000000000008C2500000000000048000000020005005C200000FC04000001000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000A002A2202280D00000A002A42534A4201000100000000000C00000076342E302E33303331390000000005006C000000A4010000237E0000100200003402000023537472696E6773000000004404000004000000235553004804000010000000234755494400000058040000A400000023426C6F620000000000000002000001471400000900000000FA013300160000010000000E00000002000000020000000D0000000C0000000100000002000000000070010100000000000600E500D20106005201D20106003000BF010F00F2010000060044008C010600C8008C010600A9008C01060039018C01060005018C0106001E018C01060071008C0106008C008C0106002C0285010A005B009E0100000000010000000000010001000100100001020000350001000100502000000000960012021E0001005320000000008618B901060001000900B90101001100B90106001900B9010A002900B90110003100B90110003900B90110004100B90110004900B90110005100B90110005900B90110006100B90110007100B90106006900B9010600200063009E002E000B0022002E0013002B002E001B004A002E00230053002E002B0061002E0033007A002E003B007A002E00430080002E004B0080002E0053007A002E005B0091000480000004000600000045560000000000001F000000040000000000000000000000150016000000000004000000000000000000000015000A00000000000000003C4D6F64756C653E0053797374656D2E44617461006D73636F726C69620053716C576174636844617461626173650044656275676761626C6541747472696275746500417373656D626C795469746C654174747269627574650053716C50726F63656475726541747472696275746500417373656D626C7954726164656D61726B41747472696275746500417373656D626C7946696C6556657273696F6E41747472696275746500417373656D626C79436F6E66696775726174696F6E41747472696275746500417373656D626C794465736372697074696F6E41747472696275746500436F6D70696C6174696F6E52656C61786174696F6E7341747472696275746500417373656D626C7950726F6475637441747472696275746500417373656D626C79436F7079726967687441747472696275746500417373656D626C79436F6D70616E794174747269627574650052756E74696D65436F6D7061746962696C6974794174747269627574650053716C576174636844617461626173652E646C6C0053797374656D0053797374656D2E5265666C656374696F6E004D6963726F736F66742E53716C5365727665722E536572766572002E63746F720053797374656D2E446961676E6F73746963730053797374656D2E52756E74696D652E436F6D70696C6572536572766963657300446562756767696E674D6F6465730053746F72656450726F636564757265730053747265616D506572666F726D616E6365436F756E74657273004F626A656374000000000000533CA73F0A7E4347AE1421EB202A29F600042001010803200001052001011111042001010E08B77A5C561934E089030000010801000800000000001E01000100540216577261704E6F6E457863657074696F6E5468726F7773010801000701000000000D01000853514C574154434800001801001368747470733A2F2F73716C77617463682E696F00000501000000001001000B53514C57415443482E494F00000C010007342E302E302E3000000401000000008025000000000000000000009A2500000020000000000000000000000000000000000000000000008C250000000000000000000000005F436F72446C6C4D61696E006D73636F7265652E646C6C0000000000FF2500200010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000100100000001800008000000000000000000000000000000100010000003000008000000000000000000000000000000100000000004800000058400000600300000000000000000000600334000000560053005F00560045005200530049004F004E005F0049004E0046004F0000000000BD04EFFE00000100000004000000000000000400000000003F000000000000000400000002000000000000000000000000000000440000000100560061007200460069006C00650049006E0066006F00000000002400040000005400720061006E0073006C006100740069006F006E00000000000000B004C0020000010053007400720069006E006700460069006C00650049006E0066006F0000009C020000010030003000300030003000340062003000000040001400010043006F006D006D0065006E00740073000000680074007400700073003A002F002F00730071006C00770061007400630068002E0069006F00000022000100010043006F006D00700061006E0079004E0061006D00650000000000000000003A0009000100460069006C0065004400650073006300720069007000740069006F006E0000000000530051004C005700410054004300480000000000300008000100460069006C006500560065007200730069006F006E000000000034002E0030002E0030002E00300000004A001500010049006E007400650072006E0061006C004E0061006D0065000000530071006C0057006100740063006800440061007400610062006100730065002E0064006C006C00000000003C000C0001004C006500670061006C0043006F0070007900720069006700680074000000530051004C00570041005400430048002E0049004F0000002A00010001004C006500670061006C00540072006100640065006D00610072006B00730000000000000000005200150001004F0072006900670069006E0061006C00460069006C0065006E0061006D0065000000530071006C0057006100740063006800440061007400610062006100730065002E0064006C006C000000000038000C000100500072006F0064007500630074004E0061006D00650000000000530051004C00570041005400430048002E0049004F000000340008000100500072006F006400750063007400560065007200730069006F006E00000034002E0030002E0030002E003000000040000C00010041007300730065006D0062006C0079002000560065007200730069006F006E00000034002E0036002E0030002E00320032003000380035000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000002000000C000000AC3500000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000
WITH PERMISSION_SET = UNSAFE
GO

CREATE TYPE [dbo].[utype_event_data] AS TABLE(
	[event_data] [xml] NULL,
	[object_name] [nvarchar](256) NULL,
	[event_time] [datetime2](0) NULL
)
GO

CREATE TYPE [dbo].[utype_plan_handle] AS TABLE(
	[plan_handle] [varbinary](64) NULL,
	[statement_start_offset] [int] NULL,
	[statement_end_offset] [int] NULL,
	[sql_handle] [varbinary](64) NULL
)
GO

CREATE TYPE [dbo].[utype_plan_id] AS TABLE(
	[sqlwatch_query_plan_id] [int] NOT NULL,
	[query_hash] [varbinary](8) NOT NULL,
	[query_plan_hash] [varbinary](8) NOT NULL,
	[action] [varchar](50) NULL
)
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE FUNCTION [dbo].[ufn_sqlwatch_get_agent_status]
()
RETURNS bit
AS
BEGIN
	return (
		select case when (
			select count(*) 
			from master.dbo.sysprocesses 
			where program_name = N'SQLAgent - Generic Refresher'
			) > 0 
			then 1 
			else 0 end
		);
END;
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE FUNCTION [dbo].[ufn_sqlwatch_get_clr_collector_status]
()
RETURNS bit 
AS
BEGIN
	RETURN (select 
		case 
			when dbo.ufn_sqlwatch_get_config_value (21, null) = 1
			and dbo.ufn_sqlwatch_get_clr_status() = 1 
			then 1 
		else 0 
		end)
END
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE FUNCTION [dbo].[ufn_sqlwatch_get_clr_status]
()
RETURNS bit
AS
BEGIN
	RETURN (select convert(bit,value_in_use) from sys.configurations where name = 'clr enabled')
END
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE FUNCTION [dbo].[ufn_sqlwatch_get_delivery_command]
(
	@address nvarchar(max),
	@title nvarchar(max),
	@content nvarchar(max),
	@attributes nvarchar(max),
	@target_type nvarchar(max)
)
returns nvarchar(max)
AS
begin
	return (select case 

		/* DEPRECATED */

		------------------------------------------------------------------------
		-- format command for sp_send_dbmail
		------------------------------------------------------------------------
		when lower(@target_type) = 'sp_send_dbmail' then
'declare @rtn int
exec @rtn = msdb.dbo.sp_send_dbmail @recipients = ''' + @address + ''',
@subject = ''' + @title + ''',
@body = ''' + replace(@content,'''','''''') + ''',
' + @attributes + '
select error=@rtn'

		------------------------------------------------------------------------
		-- format command for Pushover
		------------------------------------------------------------------------
		when lower(@target_type) = 'pushover' then
'$uri = "' + @address + '"
$parameters = @{
 ' + @attributes + '
  message = "' + @title + '
 ' + replace(@content,'''','''''') + '"}
  
  $parameters | Invoke-RestMethod -Uri $uri -Method Post'

		------------------------------------------------------------------------
		-- format command for Send-MailMessage
		------------------------------------------------------------------------
		when lower(@target_type) = 'send-mailmessage' then
'
$parameters = @{
To = "' + @address + '"
Subject = "' + @title + '"
Body = "' + @content + '"
 ' + @attributes + '
 }
Send-MailMessage @parameters'
	end)
end
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE FUNCTION [dbo].[ufn_sqlwatch_get_error_detail_text]()
RETURNS nvarchar(max)
AS
BEGIN
	return (select case when ERROR_MESSAGE() is not null then (
		select 'ERROR_NUMBER=' + isnull(convert(nvarchar(max),ERROR_NUMBER()),'') + char(10) + 
'ERROR_SEVERITY=' + isnull(convert(nvarchar(max),ERROR_SEVERITY()),'') + char(10) + 
'ERROR_STATE=' + isnull(convert(nvarchar(max),ERROR_STATE()),'') + char(10) + 
'ERROR_PROCEDURE=''' + isnull(convert(nvarchar(max),ERROR_PROCEDURE()),'') + '''' + char(10) + 
'ERROR_LINE=' + isnull(convert(nvarchar(max),ERROR_LINE()),'') + char(10) + 
'ERROR_MESSAGE=''' + isnull(convert(nvarchar(max),ERROR_MESSAGE()),'') + ''''
		) else null end
		)

END
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE FUNCTION [dbo].[ufn_sqlwatch_get_error_detail_xml]()
RETURNS xml
AS
BEGIN
	return (select case when ERROR_MESSAGE() is not null then (
		select	ERROR_NUMBER=ERROR_NUMBER(),
				ERROR_SEVERITY=ERROR_SEVERITY(),
				ERROR_STATE=ERROR_STATE(),
				ERROR_PROCEDURE=ERROR_PROCEDURE(), 
				ERROR_LINE=ERROR_LINE(),
				ERROR_MESSAGE=ERROR_MESSAGE()
		for xml path ('ERROR')
		) else null end)
END
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE FUNCTION [dbo].[ufn_sqlwatch_get_product_version]
(
	@type varchar(50)
)
returns decimal(10,2) as
begin
	return (select case 
		when upper(@type) = 'MAJOR' then substring(product_version, 1,charindex('.', product_version) + 1 )
		when upper(@type) = 'MINOR' then parsename(convert(varchar(32), product_version), 2)
		end
	from (select product_version=convert(nvarchar(128),serverproperty('productversion'))) t
	)
end
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE FUNCTION [dbo].[ufn_sqlwatch_get_time_group]
(
	@report_time as datetime2(0),
	@time_internval_minutes int
)
RETURNS smalldatetime
AS
BEGIN
	RETURN convert(smalldatetime,dateadd(minute,(datediff(minute,0, @report_time)/ @time_internval_minutes) * @time_internval_minutes,0))
END
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE FUNCTION [dbo].[ufn_sqlwatch_get_version]()
RETURNS @returntable TABLE
(
	major int,
	minor int,
	patch int,
	build int
)

AS
BEGIN	
	insert into @returntable
	  select top 1 parsename([value],4), parsename([value],3), parsename([value],2), parsename([value],1) 
	  from (
		select [value] = ltrim(rtrim(replace(replace(convert(varchar(max),[value]),char(10),''),char(13),'')))
		from sys.extended_properties
		where name = 'SQLWATCH Version'

		union 

		/* failsave if we have no extended property */
		select [sqlwatch_version]
		from (
				select top 1 [sqlwatch_version]
				from [dbo].[sqlwatch_app_version]
				order by [install_sequence] desc	
			) v
		) t


	RETURN
END
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE FUNCTION [dbo].[ufn_sqlwatch_get_xes_exec_count]
(
	@session_name nvarchar(64),
	@mode bit = null
)
RETURNS bigint
AS
BEGIN
		--the mode parameter will allow us to run this function in two ways:
		--0 get the count from the xe session
		--1 get the count from our stage table for comparison

		declare @execution_count bigint = 0,
				@address varbinary(8),
				@return varchar(10);

		--we're getting session address in a separate batch
		--becuase when we join xe_sessions with xe_session_targets
		--the execution goes up to 500ms. two batches run in 4 ms.

		if @mode = 0
			begin
				select @address = address 
				from sys.dm_xe_sessions
				where name = @session_name
				option (keepfixed plan)

				select @execution_count = isnull(execution_count,0)
				from sys.dm_xe_session_targets t
				where event_session_address = @address
				and target_name = 'event_file'
				option (keepfixed plan)
			end
		else
			begin
				select @execution_count = execution_count
				from [dbo].[sqlwatch_stage_xes_exec_count]
				where session_name = @session_name
				option (keep plan)
			end

	RETURN @execution_count
END
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE FUNCTION [dbo].[ufn_sqlwatch_get_xes_target_file]
(
	@session_name varchar(255)
)
returns varchar(255)
as
begin
		return (select convert(xml,[target_data]).value('(/EventFileTarget/File/@name)[1]', 'varchar(8000)')
				from sys.dm_xe_session_targets
				where [target_name] = 'event_file' 
				and [event_session_address] = (
					select [address]
					from sys.dm_xe_sessions 
					where [name] = @session_name
					)
			)
end
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE FUNCTION [dbo].[ufn_sqlwatch_parse_job_name]
(
	@client_app_name nvarchar(128),
	@job_name nvarchar(128) = null
)
RETURNS nvarchar(128) 
AS
/* 
	this function will parse the agent job name and replace the binary string:
		(SQLAgent - TSQL JobStep (Job 0x583FB91A7B48A64E96B7FDDEBDC58EC0 : Step 1)) 
	with the actual job name:
		(SQLAgent - TSQL JobStep (Job SQLWATCH-SAMPLE-JOB : Step 1)) 
*/
BEGIN
	if @job_name is null
		begin
			declare @job_id uniqueidentifier = [dbo].[ufn_sqlwatch_parse_job_id] (@client_app_name);
			select @job_name = name from msdb.dbo.sysjobs where job_id = @job_id;
		end

	RETURN (
		select case 
			when @client_app_name like 'SQLAGent - TSQL JobStep%' 
			then replace(@client_app_name collate DATABASE_DEFAULT,left(replace(@client_app_name collate DATABASE_DEFAULT,'SQLAgent - TSQL JobStep (Job ',''),34),@job_name) 
			else @client_app_name
			end
			);
END
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE FUNCTION [dbo].[ufn_sqlwatch_split_string]
(
	@input_string nvarchar(max),
	@delimiter nvarchar(max) = ','
)
RETURNS @output TABLE
(
	[value] nvarchar(max),
	[seq] smallint
)
as
begin

      declare @string nvarchar(max)
      declare @cnt Int 
	  declare @rn smallint = 1

      if(@input_string is not null) 

      begin
            set @cnt = charindex(@delimiter,@input_string) 
            while @cnt > 0 
            begin 
                  set @string = substring(@input_string,1,@cnt-1) 
                  set @input_string = substring(@input_string,@cnt+1,len(@input_string)-@cnt) 

                  insert into @output values (@string,@rn) 
                  set @cnt = charindex(@delimiter,@input_string) 
				  set @rn = @rn + 1
            end 


            set @string = @input_string 

            insert into @output values (@string,@rn) 
      end
      return
end
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_logger_perf_os_wait_stats](
	[wait_type_id] [smallint] NOT NULL,
	[waiting_tasks_count] [real] NOT NULL,
	[wait_time_ms] [real] NOT NULL,
	[max_wait_time_ms] [real] NOT NULL,
	[signal_wait_time_ms] [real] NOT NULL,
	[snapshot_time] [datetime2](0) NOT NULL,
	[snapshot_type_id] [tinyint] NOT NULL,
	[sql_instance] [varchar](32) NOT NULL,
	[waiting_tasks_count_delta] [real] NOT NULL,
	[wait_time_ms_delta] [real] NOT NULL,
	[max_wait_time_ms_delta] [real] NOT NULL,
	[signal_wait_time_ms_delta] [real] NOT NULL,
	[delta_seconds] [int] NOT NULL,
 CONSTRAINT [pk_sql_perf_mon_wait_stats] PRIMARY KEY CLUSTERED 
(
	[snapshot_time] ASC,
	[snapshot_type_id] ASC,
	[sql_instance] ASC,
	[wait_type_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, DATA_COMPRESSION = PAGE) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_logger_snapshot_header](
	[snapshot_time] [datetime2](0) NOT NULL,
	[snapshot_type_id] [tinyint] NOT NULL,
	[sql_instance] [varchar](32) NOT NULL,
	[report_time] [smalldatetime] NULL,
	[snapshot_time_utc_offset] [smallint] NOT NULL,
 CONSTRAINT [pk_snapshot] PRIMARY KEY CLUSTERED 
(
	[snapshot_time] ASC,
	[sql_instance] ASC,
	[snapshot_type_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, DATA_COMPRESSION = PAGE) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_meta_wait_stats](
	[sql_instance] [varchar](32) NOT NULL,
	[wait_type] [nvarchar](60) NOT NULL,
	[wait_type_id] [smallint] IDENTITY(1,1) NOT NULL,
	[is_excluded] [bit] NULL,
	[date_updated] [datetime] NOT NULL,
 CONSTRAINT [pk_sqlwatch_meta_wait_stats] PRIMARY KEY CLUSTERED 
(
	[sql_instance] ASC,
	[wait_type_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY],
 CONSTRAINT [uq_sqlwatch_meta_wait_stats_wait_type] UNIQUE NONCLUSTERED 
(
	[sql_instance] ASC,
	[wait_type] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE VIEW [dbo].[vw_sqlwatch_meta_wait_stats_category] with schemabinding
	AS 
	select 
		  [sql_instance]
		, [wait_type]
		, [wait_type_id]
		-- reference: https://github.com/microsoft/tigertoolbox/blob/master/Waits-and-Latches/view_Waits.sql
		-- as of 2019-11-17, commit b883496 on 25 Jun
		, [wait_category] = case when wait_type = N'SOS_SCHEDULER_YIELD' then N'CPU' 
		when wait_type = N'THREADPOOL' then 'CPU - Unavailable Worker Threads'
		when wait_type like N'LCK_%' OR wait_type = N'LOCK' then N'Lock' 
		when wait_type like N'LATCH_%' then N'Latch' 
		when wait_type like N'PAGELATCH_%' then N'Buffer Latch' 
		when wait_type like N'PAGEIOLATCH_%' then N'Buffer IO' 
		when wait_type like N'HADR_SYNC_COMMIT' then N'Always On - Secondary Synch' 
		when wait_type like N'HADR_%' OR wait_type like N'PWAIT_HADR_%' then N'Always On'
		when wait_type like N'FFT_%' then N'FileTable'
		when wait_type like N'RESOURCE_SEMAPHORE_%' OR wait_type like N'RESOURCE_SEMAPHORE_QUERY_COMPILE' then N'Memory - Compilation'
		when wait_type in (N'UTIL_PAGE_ALLOC', N'SOS_VIRTUALMEMORY_LOW', N'SOS_RESERVEDMEMBLOCKLIST', N'RESOURCE_SEMAPHORE', N'CMEMTHREAD', N'CMEMPARTITIONED', N'EE_PMOLOCK', N'MEMORY_ALLOCATION_EXT', N'RESERVED_MEMORY_ALLOCATION_EXT', N'MEMORY_GRANT_UPDATE') then N'Memory'
		when wait_type like N'CLR%' OR wait_type like N'SQLCLR%' then N'SQL CLR' 
		when wait_type like N'DBMIRROR%' OR wait_type = N'MIRROR_SEND_MESSAGE' then N'Mirroring' 
		when wait_type like N'XACT%' or wait_type like N'DTC%' or wait_type like N'TRAN_MARKLATCH_%' or wait_type like N'MSQL_XACT_%' or wait_type = N'TRANSACTION_MUTEX' then N'Transaction' 
		-- when wait_type like N'SLEEP_%' or wait_type in (N'LAZYWRITER_SLEEP', N'SQLTRACE_BUFFER_FLUSH', N'SQLTRACE_INCREMENTAL_FLUSH_SLEEP', N'SQLTRACE_WAIT_ENTRIES', N'FT_IFTS_SCHEDULER_IDLE_WAIT', N'XE_DISPATCHER_WAIT', N'REQUEST_FOR_DEADLOCK_SEARCH', N'LOGMGR_QUEUE', N'ONDEMAND_TASK_QUEUE', N'CHECKPOINT_QUEUE', N'XE_TIMER_EVENT') then N'Idle' 
		when wait_type like N'PREEMPTIVE_%' then N'External APIs or XPs' 
		when wait_type like N'BROKER_%' AND wait_type <> N'BROKER_RECEIVE_WAITFOR' then N'Service Broker' 
		when wait_type in (N'LOGMGR', N'LOGBUFFER', N'LOGMGR_RESERVE_APPEND', N'LOGMGR_FLUSH', N'LOGMGR_PMM_LOG', N'CHKPT', N'WRITELOG') then N'Tran Log IO' 
		when wait_type in (N'ASYNC_NETWORK_IO', N'NET_WAITFOR_PACKET', N'PROXY_NETWORK_IO', N'EXTERNAL_SCRIPT_NETWORK_IO') then N'Network IO' 
		when wait_type in (N'CXPACKET', N'EXCHANGE', N'CXCONSUMER') then N'CPU - Parallelism'
		when wait_type in (N'WAITFOR', N'WAIT_FOR_RESULTS', N'BROKER_RECEIVE_WAITFOR') then N'User Wait' 
		when wait_type in (N'TRACEWRITE', N'SQLTRACE_LOCK', N'SQLTRACE_FILE_BUFFER', N'SQLTRACE_FILE_WRITE_IO_COMPLETION', N'SQLTRACE_FILE_READ_IO_COMPLETION', N'SQLTRACE_PENDING_BUFFER_WRITERS', N'SQLTRACE_SHUTDOWN', N'QUERY_TRACEOUT', N'TRACE_EVTNOTIF') then N'Tracing' 
		when wait_type like N'FT_%' OR wait_type in (N'FULLTEXT GATHERER', N'MSSEARCH', N'PWAIT_RESOURCE_SEMAPHORE_FT_PARALLEL_QUERY_SYNC') then N'Full Text Search' 
		when wait_type in (N'ASYNC_IO_COMPLETION', N'IO_COMPLETION', N'WRITE_COMPLETION', N'IO_QUEUE_LIMIT', /*N'HADR_FILESTREAM_IOMGR_IOCOMPLETION',*/ N'IO_RETRY') then N'Other Disk IO' 
		when wait_type in (N'BACKUPIO', N'BACKUPBUFFER') then 'Backup IO'
		when wait_type like N'SE_REPL_%' or wait_type like N'REPL_%'  or wait_type in (N'REPLICA_WRITES', N'FCB_REPLICA_WRITE', N'FCB_REPLICA_READ', N'PWAIT_HADRSIM') then N'Replication' 
		when wait_type in (N'LOG_RATE_GOVERNOR', N'POOL_LOG_RATE_GOVERNOR', N'HADR_THROTTLE_LOG_RATE_GOVERNOR', N'INSTANCE_LOG_RATE_GOVERNOR') then N'Log Rate Governor'
		-- when wait_type like N'SLEEP_%' OR wait_type IN(N'LAZYWRITER_SLEEP', N'SQLTRACE_BUFFER_FLUSH', N'WAITFOR', N'WAIT_FOR_RESULTS', N'SQLTRACE_INCREMENTAL_FLUSH_SLEEP', N'SLEEP_TASK', N'SLEEP_SYSTEMTASK') then N'Sleep'
		when wait_type = N'REPLICA_WRITE' then 'Snapshots'
		when wait_type = N'WAIT_XTP_OFFLINE_CKPT_LOG_IO' OR wait_type = N'WAIT_XTP_CKPT_CLOSE' then 'In-Memory OLTP Logging'
		when wait_type like N'QDS%' then N'Query Store'
		when wait_type like N'XTP%' OR wait_type like N'WAIT_XTP%' then N'In-Memory OLTP'
		when wait_type like N'PARALLEL_REDO%' then N'Parallel Redo'
		when wait_type like N'COLUMNSTORE%' then N'Columnstore'
	else N'Other' end 
	from [dbo].[sqlwatch_meta_wait_stats]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE VIEW [dbo].[vw_sqlwatch_report_fact_perf_os_wait_stats] with schemabinding
as
select report_time, d.[sql_instance], m.wait_type
, [waiting_tasks_count_delta], [wait_time_ms_delta], [max_wait_time_ms_delta], [signal_wait_time_ms_delta]
, wait_category = isnull(m.wait_category,'Other')
, report_include = convert(bit,1)
 --for backward compatibility with existing pbi, this column will become report_time as we could be aggregating many snapshots in a report_period
, d.snapshot_time
, d.snapshot_type_id
, d.wait_type_id
, wait_time_ms_per_second = wait_time_ms_delta / [delta_seconds]
from [dbo].[sqlwatch_logger_perf_os_wait_stats] d
  	
	inner join dbo.sqlwatch_logger_snapshot_header h
		on  h.snapshot_time = d.[snapshot_time]
		and h.snapshot_type_id = d.snapshot_type_id
		and h.sql_instance = d.sql_instance
	
	left join dbo.vw_sqlwatch_meta_wait_stats_category m
		on m.sql_instance = d.sql_instance
		and m.wait_type_id = d.wait_type_id
	
	-- NO LONGER NEEDED:
	--left join [dbo].[sqlwatch_config_wait_stats] cw
	--	on cw.wait_type = m.wait_type
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_logger_whoisactive](
	[sqlwatch_whoisactive_record_id] [bigint] IDENTITY(1,1) NOT NULL,
	[snapshot_time] [datetime2](0) NOT NULL,
	[start_time] [datetime] NOT NULL,
	[session_id] [smallint] NOT NULL,
	[status] [varchar](30) NOT NULL,
	[percent_complete] [varchar](30) NULL,
	[host_name] [nvarchar](128) NULL,
	[database_name] [nvarchar](128) NULL,
	[program_name] [nvarchar](128) NULL,
	[sql_text] [xml] NULL,
	[sql_command] [xml] NULL,
	[login_name] [nvarchar](128) NOT NULL,
	[open_tran_count] [varchar](30) NULL,
	[wait_info] [nvarchar](4000) NULL,
	[blocking_session_id] [smallint] NULL,
	[blocked_session_count] [varchar](30) NULL,
	[CPU] [varchar](30) NULL,
	[used_memory] [varchar](30) NULL,
	[tempdb_current] [varchar](30) NULL,
	[tempdb_allocations] [varchar](30) NULL,
	[reads] [varchar](30) NULL,
	[writes] [varchar](30) NULL,
	[physical_reads] [varchar](30) NULL,
	[login_time] [datetime] NULL,
	[snapshot_type_id] [tinyint] NOT NULL,
	[sql_instance] [varchar](32) NOT NULL,
 CONSTRAINT [pk_sqlwatch_logger_whoisactive] PRIMARY KEY CLUSTERED 
(
	[sql_instance] ASC,
	[snapshot_time] ASC,
	[sqlwatch_whoisactive_record_id] ASC,
	[snapshot_type_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE VIEW [dbo].[vw_sqlwatch_report_fact_whoisactive] with schemabinding
as
SELECT [sqlwatch_whoisactive_record_id]
      ,report_time
      ,[start_time]
      ,[session_id]
      ,[status]
      ,[percent_complete]
      ,[host_name]
      ,[database_name]
      ,[program_name]
      ,[sql_text] = replace(replace(convert(nvarchar(max),[sql_text]),'<?query --
',''),'
--?>','')
      ,[sql_command] = replace(replace(convert(nvarchar(max),[sql_command]),'<?query --
',''),'
--?>','')
      ,[login_name]
      ,[open_tran_count]=convert(real,[open_tran_count])
      ,[wait_info]
      ,[blocking_session_id]=convert(bigint,[blocking_session_id])
      ,[blocked_session_count]=convert(bigint,[blocked_session_count])
      ,[CPU] = convert(real,replace([CPU],',',''))
      ,[used_memory] = convert(real,replace([used_memory],',',''))
      ,[tempdb_current] = convert(real,replace([tempdb_current],',',''))
      ,[tempdb_allocations] = convert(real,replace([tempdb_allocations],',',''))
      ,[reads] = convert(real,replace([reads],',',''))
      ,[writes] = convert(real,replace([writes],',',''))
      ,[physical_reads] = convert(real,replace([physical_reads],',',''))
      ,[login_time]
      ,d.[sql_instance]
	  ,[wait_type] = case when [wait_info] like '%:%' then substring(substring([wait_info],patindex('%)%',[wait_info])+1,len([wait_info])),1,patindex('%:%',substring([wait_info],patindex('%)%',[wait_info]),len([wait_info])))-2) else substring([wait_info],patindex('%)%',[wait_info])+1,len([wait_info])) end
	  ,[wait_time_ms] = convert(bigint,substring([wait_info],2,patindex('%)%',[wait_info])-4))
      ,[end_time] = case 
				when convert(bigint,substring([wait_info],2,patindex('%)%',[wait_info])-4)) >= 2147483648 then
					/* Dateadd can only handle integers, for very long running processes we have to drop resolution and convert to seconds to avoid overlfow. 
					   https://github.com/marcingminski/sqlwatch/issues/148 */
					dateadd(s,(convert(bigint,substring([wait_info],2,patindex('%)%',[wait_info])-4))/1000.0),convert(datetime,ltrim([start_time])))
				else
					dateadd(ms,convert(bigint,substring([wait_info],2,patindex('%)%',[wait_info])-4)),convert(datetime,ltrim([start_time])))
				end
      ,[last_collection] = case when ROW_NUMBER() over (partition by [session_id], [start_time] order by d.[snapshot_time] desc) = 1 then 1 else 0 end
      ,[session_id_global] = dense_rank() over (order by [start_time], [session_id])
      ,[lapsed_seconds] = datediff(s,[start_time],d.[snapshot_time])
	 --for backward compatibility with existing pbi, this column will become report_time as we could be aggregating many snapshots in a report_period
	, d.snapshot_time
	, d.snapshot_type_id
  FROM [dbo].[sqlwatch_logger_whoisactive] d
  	inner join dbo.sqlwatch_logger_snapshot_header h
		on  h.snapshot_time = d.[snapshot_time]
		and h.snapshot_type_id = d.snapshot_type_id
		and h.sql_instance = d.sql_instance
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_logger_xes_iosubsystem](
	[event_time] [datetime] NOT NULL,
	[io_latch_timeouts] [bigint] NULL,
	[total_long_ios] [bigint] NULL,
	[longest_pending_request_file] [varchar](255) NULL,
	[longest_pending_request_duration] [bigint] NULL,
	[snapshot_time] [datetime2](0) NOT NULL,
	[snapshot_type_id] [tinyint] NOT NULL,
	[sql_instance] [varchar](32) NOT NULL,
 CONSTRAINT [pk_logger_performance_xes_iosubsystem] PRIMARY KEY CLUSTERED 
(
	[snapshot_time] ASC,
	[snapshot_type_id] ASC,
	[sql_instance] ASC,
	[event_time] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, DATA_COMPRESSION = PAGE) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE VIEW [dbo].[vw_sqlwatch_report_fact_xes_iosubsystem] with schemabinding
as
SELECT [event_time]
      ,[io_latch_timeouts]
      ,[total_long_ios]
      ,[longest_pending_request_file]
      ,[longest_pending_request_duration]
      ,report_time
      ,d.[sql_instance]
 --for backward compatibility with existing pbi, this column will become report_time as we could be aggregating many snapshots in a report_period
, d.snapshot_time
, d.snapshot_type_id
  FROM [dbo].[sqlwatch_logger_xes_iosubsystem] d
  	inner join dbo.sqlwatch_logger_snapshot_header h
		on  h.snapshot_time = d.[snapshot_time]
		and h.snapshot_type_id = d.snapshot_type_id
		and h.sql_instance = d.sql_instance
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_logger_xes_query_processing](
	[event_time] [datetime] NOT NULL,
	[max_workers] [bigint] NULL,
	[workers_created] [bigint] NULL,
	[idle_workers] [bigint] NULL,
	[pending_tasks] [bigint] NULL,
	[unresolvable_deadlocks] [int] NULL,
	[deadlocked_scheduler] [int] NULL,
	[snapshot_time] [datetime2](0) NOT NULL,
	[snapshot_type_id] [tinyint] NOT NULL,
	[sql_instance] [varchar](32) NOT NULL,
 CONSTRAINT [pk_logger_xe_query_processing] PRIMARY KEY CLUSTERED 
(
	[snapshot_time] ASC,
	[snapshot_type_id] ASC,
	[sql_instance] ASC,
	[event_time] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, DATA_COMPRESSION = PAGE) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE VIEW [dbo].[vw_sqlwatch_report_fact_xes_query_processing] with schemabinding
as
SELECT [event_time]
      ,[max_workers]
      ,[workers_created]
      ,[idle_workers]
      ,[pending_tasks]
      ,[unresolvable_deadlocks]
      ,[deadlocked_scheduler]
      ,report_time
      ,d.[sql_instance]
 --for backward compatibility with existing pbi, this column will become report_time as we could be aggregating many snapshots in a report_period
, d.snapshot_time
, d.snapshot_type_id
  FROM [dbo].[sqlwatch_logger_xes_query_processing] d
  	inner join dbo.sqlwatch_logger_snapshot_header h
		on  h.snapshot_time = d.[snapshot_time]
		and h.snapshot_type_id = d.snapshot_type_id
		and h.sql_instance = d.sql_instance
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_logger_hadr_database_replica_states](
	[hadr_group_name] [nvarchar](128) NOT NULL,
	[replica_server_name] [nvarchar](128) NOT NULL,
	[availability_mode] [tinyint] NULL,
	[failover_mode] [tinyint] NULL,
	[database_name] [nvarchar](128) NOT NULL,
	[is_local] [bit] NULL,
	[is_primary_replica] [bit] NULL,
	[synchronization_state] [tinyint] NULL,
	[is_commit_participant] [bit] NULL,
	[synchronization_health] [tinyint] NULL,
	[database_state] [tinyint] NULL,
	[is_suspended] [bit] NULL,
	[suspend_reason] [bit] NULL,
	[log_send_queue_size] [bit] NULL,
	[log_send_rate] [real] NULL,
	[redo_queue_size] [real] NULL,
	[redo_rate] [real] NULL,
	[filestream_send_rate] [real] NULL,
	[secondary_lag_seconds] [real] NULL,
	[last_commit_time] [datetime] NULL,
	[snapshot_type_id] [tinyint] NOT NULL,
	[snapshot_time] [datetime2](0) NOT NULL,
	[sql_instance] [varchar](32) NOT NULL,
 CONSTRAINT [pk_sqlwatch_logger_hadr_database_replica_states] PRIMARY KEY CLUSTERED 
(
	[hadr_group_name] ASC,
	[replica_server_name] ASC,
	[database_name] ASC,
	[snapshot_time] ASC,
	[sql_instance] ASC,
	[snapshot_type_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, DATA_COMPRESSION = PAGE) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE VIEW [dbo].[vw_sqlwatch_report_hadr_database_replica_states]
WITH SCHEMABINDING AS

--https://docs.microsoft.com/en-us/sql/relational-databases/system-dynamic-management-views/sys-dm-hadr-database-replica-states-transact-sql
--https://docs.microsoft.com/en-us/sql/relational-databases/system-catalog-views/sys-availability-replicas-transact-sql
select 
	  [hadr_group_name]
	, [replica_server_name]
	, [availability_mode]
	, [availability_mode_desc] = case [availability_mode]
			when 0 then 'ASYNCHRONOUS_COMMIT'
			when 1 then 'SYNCHRONOUS_COMMIT'
			when 4 then 'CONFIGURATION_ONLY'
			else convert(varchar(max),[availability_mode]) end
	, [failover_mode]
	, [failover_mode_desc] = case [failover_mode]
			when 0 then 'AUTOMATIC'
			when 1 then 'MANUAL'
			else convert(varchar(max),[failover_mode]) end
	, [database_name]
	, [is_local]
	, [is_primary_replica]
	, [synchronization_state]
	, [synchronization_state_desc] = case [synchronization_state]
			when 0 then 'NOT SYNCHRONIZING'
			when 1 then 'SYNCHRONIZING'
			when 2 then 'SYNCHRONIZED'
			when 3 then 'REVERTING'
			when 4 then 'INITIALIZING'
			else convert(varchar(max),[synchronization_state]) end
	, [is_commit_participant]
	, [synchronization_health]
	, [synchronization_health_desc] = case [synchronization_health]
			when 0 then 'NOT_HEALTHY'
			when 1 then 'PARTIALLY_HEALTHY'
			when 2 then 'HEALTHY'
			else convert(varchar(max),[synchronization_health]) end
	, [database_state] 
	, [database_state_desc] = case [database_state]
			when 0 then 'ONLINE'
			when 1 then 'RESTORING'
			when 2 then 'RECOVERING'
			when 3 then 'RECOVERY_PENDING'
			when 4 then 'SUSPECT'
			when 5 then 'EMERGENCY'
			when 6 then 'OFFLINE'
			else convert(varchar(max),[database_state]) end
	, [is_suspended]
	, [suspend_reason] 
	, [suspend_reason_desc] = case [suspend_reason]
			when 0 then 'SUSPEND_FROM_USER'
			when 1 then 'SUSPEND_FROM_PARTNER'
			when 2 then 'SUSPEND_FROM_REDO'
			when 3 then 'SUSPEND_FROM_APPLY'
			when 4 then 'SUSPEND_FROM_CAPTURE'
			when 5 then 'SUSPEND_FROM_RESTART'
			when 6 then 'SUSPEND_FROM_UNDO'
			when 7 then 'SUSPEND_FROM_REVALIDATION'
			when 8 then 'SUSPEND_FROM_XRF_UPDATE'
			else convert(varchar(max),[suspend_reason]) end

	, [log_send_queue_size]
	, [log_send_rate]
	, [redo_queue_size]
	, [redo_rate]
	, [filestream_send_rate]
	, [secondary_lag_seconds]
	, [last_commit_time]
	, [snapshot_time]
	, rs.[sql_instance]
from [dbo].[sqlwatch_logger_hadr_database_replica_states] rs
--inner join dbo.sqlwatch_meta_database db
--on db.sqlwatch_database_id = rs.sqlwatch_database_id
--and db.sql_instance = rs.sql_instance
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_meta_action_queue](
	[sql_instance] [varchar](32) NOT NULL,
	[queue_item_id] [bigint] IDENTITY(1,1) NOT NULL,
	[action_exec_type] [varchar](50) NOT NULL,
	[time_queued] [datetime2](7) NOT NULL,
	[action_exec] [varchar](max) NOT NULL,
	[exec_status] [varchar](50) NULL,
	[exec_time_start] [datetime2](7) NULL,
	[exec_time_end] [datetime2](7) NULL,
	[retry_count] [smallint] NULL,
 CONSTRAINT [pk_sqlwatch_meta_delivery_queue] PRIMARY KEY CLUSTERED 
(
	[sql_instance] ASC,
	[queue_item_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_app_log](
	[event_sequence] [bigint] IDENTITY(1,1) NOT NULL,
	[sql_instance] [varchar](32) NOT NULL,
	[event_time] [datetime2](7) NULL,
	[process_name] [nvarchar](512) NULL,
	[process_stage] [nvarchar](max) NULL,
	[process_message] [nvarchar](max) NULL,
	[process_message_type] [varchar](50) NULL,
	[spid] [int] NULL,
	[process_login] [nvarchar](512) NULL,
	[process_user] [nvarchar](512) NULL,
	[ERROR_NUMBER] [int] NULL,
	[ERROR_SEVERITY] [int] NULL,
	[ERROR_STATE] [int] NULL,
	[ERROR_PROCEDURE] [nvarchar](max) NULL,
	[ERROR_LINE] [int] NULL,
	[ERROR_MESSAGE] [nvarchar](max) NULL,
 CONSTRAINT [pk_sqlwatch_sys_log] PRIMARY KEY CLUSTERED 
(
	[event_sequence] ASC,
	[sql_instance] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_app_version](
	[install_sequence] [smallint] IDENTITY(1,1) NOT NULL,
	[install_date] [datetimeoffset](7) NOT NULL,
	[sqlwatch_version] [varchar](255) NOT NULL,
 CONSTRAINT [pk_sqlwatch_version] PRIMARY KEY CLUSTERED 
(
	[install_sequence] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_config_action](
	[action_id] [smallint] IDENTITY(1,1) NOT NULL,
	[action_description] [varchar](max) NULL,
	[action_exec_type] [varchar](50) NOT NULL,
	[action_exec] [varchar](max) NULL,
	[action_report_id] [smallint] NULL,
	[action_enabled] [bit] NOT NULL,
	[date_created] [datetime] NOT NULL,
	[date_updated] [datetime] NULL,
 CONSTRAINT [pk_sqlwatch_config_delivery_target] PRIMARY KEY CLUSTERED 
(
	[action_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_config_check_action](
	[check_id] [bigint] NOT NULL,
	[action_id] [smallint] NOT NULL,
	[action_every_failure] [bit] NOT NULL,
	[action_recovery] [bit] NOT NULL,
	[action_repeat_period_minutes] [smallint] NULL,
	[action_hourly_limit] [tinyint] NOT NULL,
	[action_template_id] [smallint] NOT NULL,
	[date_created] [datetime] NOT NULL,
	[date_updated] [datetime] NULL,
 CONSTRAINT [pk_sqlwatch_config_check_action] PRIMARY KEY CLUSTERED 
(
	[check_id] ASC,
	[action_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_meta_repository_import_status](
	[sql_instance] [varchar](32) NOT NULL,
	[object_name] [nvarchar](512) NOT NULL,
	[import_status] [varchar](50) NULL,
	[import_end_time] [datetime2](7) NULL,
	[exec_proc] [nvarchar](1024) NULL,
	[import_age_minutes]  AS (datediff(minute,[import_end_time],getdate())),
 CONSTRAINT [pk_sqlwatch_logger_repository_import] PRIMARY KEY CLUSTERED 
(
	[sql_instance] ASC,
	[object_name] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_config_snapshot_type](
	[snapshot_type_id] [tinyint] NOT NULL,
	[snapshot_type_desc] [varchar](255) NOT NULL,
	[snapshot_retention_days] [smallint] NOT NULL,
	[collect] [bit] NOT NULL,
 CONSTRAINT [pk_sqlwatch_config_snapshot_type] PRIMARY KEY CLUSTERED 
(
	[snapshot_type_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_config_sql_instance](
	[sql_instance] [varchar](32) NOT NULL,
	[hostname] [nvarchar](32) NULL,
	[sql_port] [int] NULL,
	[sqlwatch_database_name] [sysname] NOT NULL,
	[environment] [sysname] NOT NULL,
	[repo_collector_is_active] [bit] NOT NULL,
	[linked_server_name] [nvarchar](255) NULL,
	[sql_instance_user_alias] [nvarchar](128) NULL,
	[sql_user] [varchar](50) NULL,
	[sql_secret] [varchar](255) NULL,
 CONSTRAINT [pk_config_sql_instance] PRIMARY KEY CLUSTERED 
(
	[sql_instance] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE VIEW [dbo].[vw_sqlwatch_help_last_snapshot_time] with schemabinding
	as
select 
	  h.sql_instance
	, h.snapshot_type_id
	, t.snapshot_type_desc
	, snapshot_time_utc=max(snapshot_time)
	, snapshot_time_local = max(dateadd(minute,[snapshot_time_utc_offset],snapshot_time))
	, snapshot_age_minutes = datediff(minute,max(snapshot_time),getutcdate())
	, snapshot_age_hours = datediff(hour,max(snapshot_time),getutcdate())
from dbo.sqlwatch_logger_snapshot_header h
inner join dbo.sqlwatch_config_sql_instance s
	on h.sql_instance = s.sql_instance
inner join dbo.sqlwatch_config_snapshot_type t
	on t.snapshot_type_id = h.snapshot_type_id
where ( s.sql_instance = @@SERVERNAME or (s.sql_instance <> @@SERVERNAME and repo_collector_is_active = 1))
and ( (t.collect = 1 and h.sql_instance = @@SERVERNAME) or h.sql_instance <> @@SERVERNAME )
	
	----these snapshots do not have to run within given schedule:
	--and snapshot_type_id not in (	
	--19, --actions
	--20, --reports
	--21  --log
	--)

group by h.sql_instance, h.snapshot_type_id, t.snapshot_type_desc
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_meta_check](
	[sql_instance] [varchar](32) NOT NULL,
	[check_id] [bigint] NOT NULL,
	[check_name] [nvarchar](255) NULL,
	[check_description] [nvarchar](2048) NULL,
	[check_query] [nvarchar](max) NULL,
	[check_frequency_minutes] [smallint] NULL,
	[check_threshold_warning] [varchar](100) NULL,
	[check_threshold_critical] [varchar](100) NULL,
	[last_check_date] [datetime] NULL,
	[last_check_value] [real] NULL,
	[last_check_status] [varchar](50) NULL,
	[last_status_change_date] [datetime] NULL,
	[date_updated] [datetime] NOT NULL,
	[check_enabled] [bit] NOT NULL,
	[use_baseline] [bit] NOT NULL,
	[base_object_type] [varchar](50) NULL,
	[base_object_name] [nvarchar](128) NULL,
	[base_object_date_last_seen] [datetime2](0) NULL,
	[target_sql_instance] [varchar](32) NULL,
 CONSTRAINT [pk_sqlwatch_meta_alert] PRIMARY KEY CLUSTERED 
(
	[sql_instance] ASC,
	[check_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_logger_check](
	[sql_instance] [varchar](32) NOT NULL,
	[snapshot_time] [datetime2](0) NOT NULL,
	[snapshot_type_id] [tinyint] NOT NULL,
	[check_id] [bigint] NOT NULL,
	[check_value] [real] NULL,
	[check_status] [varchar](15) NOT NULL,
	[check_exec_time_ms] [real] NULL,
	[status_change] [bit] NULL,
	[is_flapping] [bit] NULL,
	[baseline_threshold] [real] NULL,
 CONSTRAINT [pk_sqlwatch_logger_check] PRIMARY KEY CLUSTERED 
(
	[snapshot_time] ASC,
	[sql_instance] ASC,
	[check_id] ASC,
	[snapshot_type_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, DATA_COMPRESSION = PAGE) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE VIEW [dbo].[vw_sqlwatch_report_dim_check] with schemabinding
	AS 
	select ma.sql_instance, ma.check_id, ma.[check_name], ma.last_check_date, ma.last_check_value, ma.last_check_status, ma.[last_status_change_date]
		, ma.check_description
		, avg_check_exec_time_ms = convert(decimal(10,2),t.check_exec_time_ms)
		, max_check_exec_time_ms = convert(decimal(10,2),t.check_exec_time_ms_max)
		, min_check_exec_time_ms = convert(decimal(10,2),t.check_exec_time_ms_min)

		, t.total_checks_executed
		, ma.check_enabled
		, ma.target_sql_instance
	from [dbo].[sqlwatch_meta_check] ma

	--get average exec time for each check
	left join (
		select sql_instance, check_id
			, check_exec_time_ms=avg(check_exec_time_ms)
			, total_checks_executed=count(check_exec_time_ms)
			, check_exec_time_ms_max=max(check_exec_time_ms)
			, check_exec_time_ms_min=min(check_exec_time_ms)
		from [dbo].[sqlwatch_logger_check]
		group by sql_instance, check_id
	) t
	on t.sql_instance = ma.sql_instance
	and t.check_id = ma.check_id
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE VIEW [dbo].[vw_sqlwatch_help_diagnostics]
as

select sqlwatch_diagnostics = (
	select 
		 sql_version = @@VERSION
		,timegerenated = convert(varchar(100),SYSDATETIMEOFFSET(),121)
		,sqlwatch_version = (
			select top 1 install_sequence, install_date =convert(varchar(100),install_date,121), sqlwatch_version
			from [dbo].[sqlwatch_app_version] 
			order by install_sequence desc
			for xml raw, type)
		,last_snapshot = (
			select 
				  [sql_instance_anonym] = master.dbo.fn_varbintohexstr(HashBytes('MD5', [sql_instance]))
				, [snapshot_type_id]
				, [snapshot_type_desc]
				, [snapshot_time_utc]
				, [snapshot_time_local]
				, [snapshot_age_minutes]
				, [snapshot_age_hours] 
			from [dbo].[vw_sqlwatch_help_last_snapshot_time]
			for xml raw, type
			)
		,default_checks = (
			select 
				  [sql_instance_anonym] = master.dbo.fn_varbintohexstr(HashBytes('MD5', [sql_instance]))
				, [check_id]
				, [check_name]
				, [last_check_date]
				, [last_check_value]
				, [last_check_status]
				, [last_status_change_date]
				, [avg_check_exec_time_ms]
				, [total_checks_executed] 
				from [dbo].[vw_sqlwatch_report_dim_check]
			where check_id < 0
			for xml raw, type
		)

		,sql_watch_jobs = (
			select 
				  sj.name
				, step_name
				, last_run_outcome = case last_run_outcome
					when 0 then 'Failed'
					when 1 then 'Succeeded'
					when 2 then 'Retry'
					when 3 then 'Canceled'
					when 5 then 'Unknown'
				end
				, last_run_duration
				, [last_run_datetime] = case when last_run_date > 0 and last_run_time > 0 then msdb.dbo.agent_datetime(last_run_date,last_run_time) else null end
			from msdb.dbo.sysjobsteps sjs
				inner join msdb.dbo.sysjobs sj
				on sjs.job_id = sj.job_id
			where command like '%sqlwatch%'
			for xml raw, type
		)

		, sqlwatch_table_size = (
			select 
				table_name = t.name,
				row_count = p.rows,
				total_space_MB = CAST(ROUND(((SUM(a.total_pages) * 8) / 1024.00), 2) AS NUMERIC(36, 2)),
				used_space_MB = CAST(ROUND(((SUM(a.used_pages) * 8) / 1024.00), 2) AS NUMERIC(36, 2)), 
				unused_space_MB = CAST(ROUND(((SUM(a.total_pages) - SUM(a.used_pages)) * 8) / 1024.00, 2) AS NUMERIC(36, 2)),
				p.[data_compression_desc]
			from sys.tables t
			inner join sys.indexes i ON t.object_id = i.object_id
			inner join sys.partitions p ON i.object_id = p.object_id AND i.index_id = p.index_id
			inner join sys.allocation_units a ON p.partition_id = a.container_id
			left join sys.schemas s ON t.schema_id = s.schema_id
			where 
				t.name NOT LIKE 'dt%' 
				AND t.is_ms_shipped = 0
				AND i.object_id > 255 
				and t.name like '%sqlwatch%'
			group by t.name, s.name, p.rows, p.[data_compression_desc]
			order by t.name
			for xml raw, type
		)

		, logger_log_error_stats = (
			select sql_instance_anonym = master.dbo.fn_varbintohexstr(HashBytes('MD5', [sql_instance]))
			, process_name, ERROR_COUNT=count(*)
			from [dbo].[sqlwatch_app_log]
			where event_time > dateadd(hour,-24,getutcdate())
			and [process_message_type] = 'ERROR'
			group by master.dbo.fn_varbintohexstr(HashBytes('MD5', [sql_instance]))
			, process_name
			for xml raw, type
		)

		, logger_log_errors = (
			select event_sequence, event_time, sql_instance_anonym = master.dbo.fn_varbintohexstr(HashBytes('MD5', [sql_instance]))
			, process_name, process_stage, [ERROR_NUMBER],[ERROR_SEVERITY],[ERROR_STATE],[ERROR_PROCEDURE],[ERROR_LINE],[ERROR_MESSAGE]
			from [dbo].[sqlwatch_app_log]
			where [event_time] > dateadd(hour,-24,getutcdate())
			and [process_message_type] = 'ERROR'
			for xml raw, type
		)

		, central_repo_import_status = (
			select sql_instance_anonym = master.dbo.fn_varbintohexstr(HashBytes('MD5', [sql_instance]))
				  ,[object_name]
				  ,[import_status]
				  ,[import_end_time]
				  ,[exec_proc]
			  from [dbo].[sqlwatch_meta_repository_import_status]
			  for xml raw, type
		)

		, enabled_actions = (
			select action_id 
			from [dbo].[sqlwatch_config_action]
			where action_enabled = 1
			for xml raw, type
	)

		, check_action = (
			SELECT cca.[check_id], cca.[action_id], cca.[action_every_failure], cca.[action_recovery], cca.[action_repeat_period_minutes]
			, cca.[action_hourly_limit], cca.[action_template_id], cca.[date_created], cca.[date_updated]
			, ca.action_enabled, ca.action_exec_type
			FROM [dbo].[sqlwatch_config_check_action] cca
				left join [dbo].[sqlwatch_config_action] ca
				on ca.action_id = cca.action_id
			for xml raw, type
		)

		, action_queue_stats = (
			SELECT [exec_status], count=count(*)
			  FROM [dbo].[sqlwatch_meta_action_queue]
			group by [exec_status]
			for xml raw, type
		)
	for xml path('diagnostics'), type
)
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_meta_baseline](
	[baseline_id] [smallint] NOT NULL,
	[sql_instance] [varchar](32) NOT NULL,
	[baseline_start] [datetime2](0) NOT NULL,
	[baseline_end] [datetime2](0) NOT NULL,
	[is_default] [bit] NOT NULL,
	[comments] [varchar](max) NULL,
	[date_updated] [datetime] NOT NULL,
 CONSTRAINT [pk_sqlwatch_meta_baseline] PRIMARY KEY CLUSTERED 
(
	[baseline_id] ASC,
	[sql_instance] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_meta_snapshot_header_baseline](
	[baseline_id] [smallint] NOT NULL,
	[snapshot_time] [datetime2](0) NOT NULL,
	[snapshot_type_id] [tinyint] NOT NULL,
	[sql_instance] [varchar](32) NOT NULL,
 CONSTRAINT [pk_sqlwatch_meta_snapshot_header_baseline] PRIMARY KEY CLUSTERED 
(
	[snapshot_time] ASC,
	[sql_instance] ASC,
	[snapshot_type_id] ASC,
	[baseline_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE FUNCTION [dbo].[ufn_sqlwatch_get_check_baseline]
(
	@check_id bigint,
	@baseline_id smallint = null,
	@sql_instance varchar(32)
)
RETURNS real with schemabinding
AS
BEGIN
	declare @default_baseline_id smallint

	if @baseline_id is null
		begin
			select @baseline_id = baseline_id 
			from [dbo].[sqlwatch_meta_baseline]
			where is_default = 1
			and sql_instance = @sql_instance
		end

		return (
		select baseline_check_value=avg(check_value)
		from [dbo].[sqlwatch_logger_check] lc

		inner join [dbo].[sqlwatch_meta_snapshot_header_baseline] b
			on b.snapshot_time = lc.snapshot_time
			and b.sql_instance = lc.sql_instance
			and b.snapshot_type_id = lc.snapshot_type_id

		where b.baseline_id = @baseline_id
			and lc.check_id = @check_id
		)
END
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_config](
	[config_id] [int] NOT NULL,
	[config_name] [varchar](255) NOT NULL,
	[config_value] [smallint] NOT NULL,
 CONSTRAINT [pk_sqlwatch_config] PRIMARY KEY CLUSTERED 
(
	[config_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE FUNCTION [dbo].[ufn_sqlwatch_get_config_value] 
(
	@config_id int = null,
	@p bit = null --backward compatibility as this used to accept two pars.
) 
RETURNS smallint with schemabinding
AS
BEGIN
	return (
			select config_value 
			from dbo.[sqlwatch_config]
			where config_id = @config_id
		);
END
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE FUNCTION [dbo].[ufn_sqlwatch_get_threshold_comparator]
(
	@threshold varchar(50)
)
RETURNS varchar(2) with schemabinding
AS
BEGIN
	declare @return varchar(2)

	if left(@threshold,2) = '<='
		begin
			set @return = '<='
		end
	else if left(@threshold,2) = '>='
		begin
			set @return = '>='
		end
	else if left(@threshold,2) = '<>'
		begin
			set @return =  '<>'
		end
	else if left(@threshold,1) = '<'
		begin
			set @return = '<'
		end
	else if left(@threshold,1) = '>'
		begin
			set @return ='>'
		end
	else if left(@threshold,1) = '='
		begin
			set @return = '='
		end

		return @return
END
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE FUNCTION [dbo].[ufn_sqlwatch_get_threshold_value]
(
	@threshold varchar(50)
)
RETURNS decimal(28,5) with schemabinding
AS
BEGIN

	return convert(decimal(28,5),replace(@threshold,[dbo].[ufn_sqlwatch_get_threshold_comparator](@threshold),''))

END
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_stage_xes_exec_count](
	[session_name] [nvarchar](64) NOT NULL,
	[execution_count] [bigint] NOT NULL,
	[last_event_time] [datetime2](0) NULL,
 CONSTRAINT [pk_sqlwatch_stage_xes_exec_count] PRIMARY KEY CLUSTERED 
(
	[session_name] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE FUNCTION [dbo].[ufn_sqlwatch_get_xes_exec_time]
(
	@session_name nvarchar(64)
)
-- TO DO TODO CAN BE REMOVED IN vNEXT
RETURNS datetime2(0) with schemabinding
AS
BEGIN
	RETURN (
		select last_event_time
		from [dbo].[sqlwatch_stage_xes_exec_count]
		where session_name = @session_name
	)
END
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_logger_disk_utilisation_database](
	[sqlwatch_database_id] [smallint] NOT NULL,
	[database_size_bytes] [bigint] NULL,
	[unallocated_space_bytes] [bigint] NULL,
	[reserved_bytes] [bigint] NULL,
	[data_bytes] [bigint] NULL,
	[index_size_bytes] [bigint] NULL,
	[unused_bytes] [bigint] NULL,
	[log_size_total_bytes] [bigint] NULL,
	[log_size_used_bytes] [bigint] NULL,
	[snapshot_time] [datetime2](0) NOT NULL,
	[snapshot_type_id] [tinyint] NOT NULL,
	[sql_instance] [varchar](32) NOT NULL,
	[unallocated_extent_page_count] [bigint] NULL,
	[allocated_extent_page_count] [bigint] NULL,
	[version_store_reserved_page_count] [bigint] NULL,
	[user_object_reserved_page_count] [bigint] NULL,
	[internal_object_reserved_page_count] [bigint] NULL,
	[mixed_extent_page_count] [bigint] NULL,
 CONSTRAINT [PK_logger_disk_util_database] PRIMARY KEY CLUSTERED 
(
	[snapshot_time] ASC,
	[snapshot_type_id] ASC,
	[sql_instance] ASC,
	[sqlwatch_database_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, DATA_COMPRESSION = PAGE) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE FUNCTION [dbo].[ufn_sqlwatch_format_bytes] 
(
	@bytes bigint
) 
RETURNS varchar(100) with schemabinding
AS
BEGIN
	RETURN (
		select case 
					--JESD21-C all unites are upper case wherease in SI standard kilo is lowercase, since this is computer science and not physics, we are sticking to the the JESD21 format
					when @bytes / 1024.0 < 1000 then convert(varchar(100),convert(decimal(10,2),@bytes / 1024.0 )) + ' KB' 
					when @bytes / 1024.0 / 1024.0 < 1000 then convert(varchar(100),convert(decimal(10,2),@bytes / 1024.0 / 1024.0)) + ' MB'
					when @bytes / 1024.0 / 1024.0 / 1024.0 < 1000 then convert(varchar(100),convert(decimal(10,2),@bytes / 1024.0 / 1024.0 / 1024.0)) + ' GB' 
					else convert(varchar(100),convert(decimal(10,2),@bytes / 1024.0 / 1024.0 / 1024.0 / 1024.0)) + ' TB' 
					end
	)
END
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_meta_database](
	[database_name] [nvarchar](128) NOT NULL,
	[database_create_date] [datetime] NOT NULL,
	[sql_instance] [varchar](32) NOT NULL,
	[sqlwatch_database_id] [smallint] IDENTITY(1,1) NOT NULL,
	[date_last_seen] [datetime] NULL,
	[is_auto_close_on] [bit] NULL,
	[is_auto_shrink_on] [bit] NULL,
	[is_auto_update_stats_on] [bit] NULL,
	[user_access] [tinyint] NULL,
	[state] [tinyint] NULL,
	[snapshot_isolation_state] [tinyint] NULL,
	[is_read_committed_snapshot_on] [bit] NULL,
	[recovery_model] [tinyint] NULL,
	[page_verify_option] [tinyint] NULL,
	[is_current] [bit] NULL,
 CONSTRAINT [PK_database] PRIMARY KEY CLUSTERED 
(
	[sql_instance] ASC,
	[sqlwatch_database_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY],
 CONSTRAINT [uq_sqlwatch_meta_database] UNIQUE NONCLUSTERED 
(
	[sql_instance] ASC,
	[database_name] ASC,
	[database_create_date] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE VIEW [dbo].[vw_sqlwatch_report_dim_database] with schemabinding
as 
with cte_database as (
		select [database_name], [database_create_date], d.[sql_instance], d.[sqlwatch_database_id], [date_last_seen] 

		, [database_size_bytes_current] = lg.[database_size_bytes]
		, [database_bytes_growth] = lg.[database_size_bytes]  - fg.[database_size_bytes]
		, total_growth_days = datediff(day,fg.snapshot_time,lg.snapshot_time)
		, [log_size_total_bytes_current] = lg.[log_size_total_bytes]
		, [log_size_bytes_growth] = lg.[log_size_total_bytes] - fg.[log_size_total_bytes]

		, data_bytes_current = lg.data_bytes
		, index_size_bytes_current = lg.index_size_bytes
		, [unused_bytes_current] = lg.unused_bytes
		, unallocated_space_bytes_current = lg.unallocated_space_bytes

		from dbo.sqlwatch_meta_database d
		
		--calculate first and last snapshot dates
		left join (
			select sql_instance, sqlwatch_database_id,
				first_snapshot_time=min(snapshot_time),
				last_snapshot_time=max(snapshot_time)
			from [dbo].[sqlwatch_logger_disk_utilisation_database]
			group by sql_instance, sqlwatch_database_id
		) h
		on h.sql_instance = d.sql_instance
		and h.sqlwatch_database_id = d.sqlwatch_database_id

		--first snapshot data
		left join [dbo].[sqlwatch_logger_disk_utilisation_database] fg
		on h.first_snapshot_time = fg.snapshot_time
		and h.sqlwatch_database_id = fg.sqlwatch_database_id
		and h.sql_instance = fg.sql_instance

		--second snapshot data
		left join [dbo].[sqlwatch_logger_disk_utilisation_database] lg
		on h.last_snapshot_time = lg.snapshot_time
		and h.sqlwatch_database_id = lg.sqlwatch_database_id
		and h.sql_instance = lg.sql_instance

		where is_current = 1
), 

cte_database_growth as (
	select [database_name], [database_create_date], [sql_instance], [sqlwatch_database_id], [date_last_seen]
		, [database_size_bytes_current], [database_bytes_growth], [total_growth_days] , [log_size_total_bytes_current], [log_size_bytes_growth]
		, database_growth_bytes_per_day = case when [total_growth_days] > 0 and [database_bytes_growth] > 0 then [database_bytes_growth] / total_growth_days else 0 end
		, log_growth_bytes_per_day = case when [total_growth_days] > 0 and [log_size_bytes_growth] > 0 then [log_size_bytes_growth] / total_growth_days else 0 end
		, data_bytes_current, index_size_bytes_current, [unused_bytes_current], unallocated_space_bytes_current
	from cte_database
)

	select [database_name], [database_create_date], [sql_instance], [sqlwatch_database_id], [date_last_seen]
		, [database_size_bytes_current], [database_bytes_growth]
		, [total_growth_days], database_growth_bytes_per_day, log_growth_bytes_per_day, [log_size_total_bytes_current]
		, data_bytes_current, index_size_bytes_current, [unused_bytes_current], unallocated_space_bytes_current

		, last_seen_days = datediff(day,date_last_seen,getutcdate())

		, [database_size_bytes_current_formatted] =  [dbo].[ufn_sqlwatch_format_bytes] ([database_size_bytes_current])
		, [growth_bytes_per_day_formatted] = [dbo].[ufn_sqlwatch_format_bytes] (database_growth_bytes_per_day) + ' / Day'
		, [log_size_bytes_current_formatted] = [dbo].[ufn_sqlwatch_format_bytes] ([log_size_total_bytes_current])
		, [log_growth_bytes_per_day_formatted] = [dbo].[ufn_sqlwatch_format_bytes] (log_growth_bytes_per_day) + ' / Day'

	from cte_database_growth
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE FUNCTION [dbo].[ufn_sqlwatch_get_file_type_desc] 
(
	@file_type tinyint
) 
RETURNS varchar(max)
with schemabinding
AS
BEGIN
/* https://docs.microsoft.com/en-us/sql/relational-databases/system-catalog-views/sys-master-files-transact-sql
		
File type:
0 = Rows.
1 = Log
2 = FILESTREAM
3 = Identified for informational purposes only. Not supported. Future compatibility is not guaranteed.
4 = Full-text (Full-text catalogs earlier than SQL Server 2008; full-text catalogs that are upgraded to or created in SQL Server 2008 or higher will report a file type 0.)

*/
		return  case @file_type
			when 0 then 'Rows'
			when 1 then 'Log'
			when 2 then 'FILESTREAM'
			when 3 then '3'
			when 4 then 'Full-text'
		else 'UNKNOWN' end
END
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_logger_perf_file_stats](
	[sqlwatch_database_id] [smallint] NOT NULL,
	[sqlwatch_master_file_id] [smallint] NOT NULL,
	[num_of_reads] [real] NOT NULL,
	[num_of_bytes_read] [real] NOT NULL,
	[io_stall_read_ms] [real] NOT NULL,
	[num_of_writes] [real] NOT NULL,
	[num_of_bytes_written] [real] NOT NULL,
	[io_stall_write_ms] [real] NOT NULL,
	[size_on_disk_bytes] [real] NOT NULL,
	[snapshot_time] [datetime2](0) NOT NULL,
	[snapshot_type_id] [tinyint] NOT NULL,
	[sql_instance] [varchar](32) NOT NULL,
	[num_of_reads_delta] [real] NULL,
	[num_of_bytes_read_delta] [real] NULL,
	[io_stall_read_ms_delta] [real] NULL,
	[num_of_writes_delta] [real] NULL,
	[num_of_bytes_written_delta] [real] NULL,
	[io_stall_write_ms_delta] [real] NULL,
	[size_on_disk_bytes_delta] [real] NULL,
	[delta_seconds] [int] NULL,
 CONSTRAINT [pk_sql_perf_mon_file_stats] PRIMARY KEY CLUSTERED 
(
	[sql_instance] ASC,
	[snapshot_time] ASC,
	[sqlwatch_database_id] ASC,
	[sqlwatch_master_file_id] ASC,
	[snapshot_type_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, DATA_COMPRESSION = PAGE) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_meta_master_file](
	[sqlwatch_database_id] [smallint] NOT NULL,
	[sqlwatch_master_file_id] [smallint] IDENTITY(1,1) NOT NULL,
	[file_id] [int] NULL,
	[file_type] [tinyint] NULL,
	[file_name] [nvarchar](260) NULL,
	[file_physical_name] [nvarchar](260) NULL,
	[sql_instance] [varchar](32) NOT NULL,
	[date_last_seen] [datetime] NULL,
	[logical_disk] [varchar](260) NULL,
 CONSTRAINT [PK_sql_perf_mon_master_files] PRIMARY KEY CLUSTERED 
(
	[sql_instance] ASC,
	[sqlwatch_database_id] ASC,
	[sqlwatch_master_file_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE VIEW [dbo].[vw_sqlwatch_report_dim_master_file] with schemabinding
	AS 
		select db.[database_name], [d].[sqlwatch_database_id], [d].[sqlwatch_master_file_id], [d].[file_id]
		, [d].[file_type]

		, file_type_desc = [dbo].[ufn_sqlwatch_get_file_type_desc](d.file_type)
		, [d].[file_name], [d].[file_physical_name], [d].[sql_instance], [d].[date_last_seen], [d].[logical_disk], size_on_disk_bytes=isnull(lg.size_on_disk_bytes,fg.size_on_disk_bytes)

		, [size_on_disk_bytes_formatted] =  [dbo].[ufn_sqlwatch_format_bytes] (isnull(lg.size_on_disk_bytes,fg.size_on_disk_bytes))

		from [dbo].[sqlwatch_meta_master_file] d

		inner join [dbo].[sqlwatch_meta_database] db
			on db.sqlwatch_database_id = d.sqlwatch_database_id
			and db.sql_instance = d.sql_instance
			and db.is_current = 1

		-- get first and last snapshots
		left join (
				select sql_instance, [sqlwatch_database_id], [sqlwatch_master_file_id]
					, first_snapshot_time=min(snapshot_time)
					, last_snapshot_time=max(snapshot_time) 
				from [dbo].[sqlwatch_logger_perf_file_stats]
				group by sql_instance, [sqlwatch_database_id], [sqlwatch_master_file_id]
		) h
		on h.sql_instance = d.sql_instance
		and h.sqlwatch_database_id = d.sqlwatch_database_id
		and h.sqlwatch_master_file_id = d.sqlwatch_master_file_id

		-- get first snapshot data
		left join [dbo].[sqlwatch_logger_perf_file_stats] fg
		on h.sql_instance = fg.sql_instance
		and h.[sqlwatch_database_id] = fg.[sqlwatch_database_id]
		and h.[sqlwatch_master_file_id] = fg.[sqlwatch_master_file_id]
		and h.first_snapshot_time = fg.snapshot_time

		-- get last snapshot data
		left join [dbo].[sqlwatch_logger_perf_file_stats] lg
		on h.sql_instance = lg.sql_instance
		and h.[sqlwatch_database_id] = lg.[sqlwatch_database_id]
		and h.[sqlwatch_master_file_id] = lg.[sqlwatch_master_file_id]
		and h.last_snapshot_time = lg.snapshot_time
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_logger_disk_utilisation_volume](
	[sqlwatch_volume_id] [smallint] NOT NULL,
	[volume_free_space_bytes] [bigint] NULL,
	[volume_total_space_bytes] [bigint] NULL,
	[snapshot_time] [datetime2](0) NOT NULL,
	[snapshot_type_id] [tinyint] NOT NULL,
	[sql_instance] [varchar](32) NOT NULL,
 CONSTRAINT [PK_disk_util_vol] PRIMARY KEY CLUSTERED 
(
	[snapshot_time] ASC,
	[snapshot_type_id] ASC,
	[sql_instance] ASC,
	[sqlwatch_volume_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, DATA_COMPRESSION = PAGE) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_meta_os_volume](
	[sql_instance] [varchar](32) NOT NULL,
	[sqlwatch_volume_id] [smallint] IDENTITY(1,1) NOT NULL,
	[volume_name] [nvarchar](255) NOT NULL,
	[label] [nvarchar](255) NOT NULL,
	[file_system] [varchar](255) NOT NULL,
	[volume_block_size_bytes] [int] NULL,
	[date_created] [datetime] NOT NULL,
	[date_updated] [datetime] NULL,
	[date_last_seen] [datetime] NULL,
	[is_record_deleted] [bit] NULL,
 CONSTRAINT [pk_sqlwatch_meta_os_volume] PRIMARY KEY CLUSTERED 
(
	[sql_instance] ASC,
	[sqlwatch_volume_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE VIEW [dbo].[vw_sqlwatch_report_dim_os_volume] with schemabinding
	AS 
	with cte_volume as (
		select d.[sql_instance], d.[sqlwatch_volume_id], d.[volume_name], d.[label], d.[file_system], d.[volume_block_size_bytes], d.[date_created], d.[date_last_seen] 
		, volume_total_space_bytes_current = lg.[volume_total_space_bytes]
		, volume_free_space_bytes_current = lg.[volume_free_space_bytes]

		, volume_bytes_growth = fg.[volume_free_space_bytes] - lg.[volume_free_space_bytes]
		, total_growth_days = datediff(day,fg.snapshot_time,lg.snapshot_time)

		, [free_space_percentage] = lg.[volume_free_space_bytes] * 1.0 / lg.[volume_total_space_bytes]
		, [is_record_deleted]
		from dbo.sqlwatch_meta_os_volume d

		-- get first and last snapshot dates
		left join (
			select [sql_instance], [sqlwatch_volume_id],
				first_snapshot_time=min(snapshot_time),
				last_snapshot_time=max(snapshot_time)
			from [dbo].[sqlwatch_logger_disk_utilisation_volume]
			group by [sql_instance], [sqlwatch_volume_id]
		) h
		on h.sql_instance = d.sql_instance
		and h.sqlwatch_volume_id = d.sqlwatch_volume_id

		-- get first snapshot data
		left join [dbo].[sqlwatch_logger_disk_utilisation_volume] fg
		on h.[sql_instance] = fg.[sql_instance]
		and h.[sqlwatch_volume_id] = fg.[sqlwatch_volume_id]
		and h.first_snapshot_time = fg.snapshot_time

		-- get last snapshot data
		left join [dbo].[sqlwatch_logger_disk_utilisation_volume] lg
		on h.[sql_instance] = lg.[sql_instance]
		and h.[sqlwatch_volume_id] = lg.[sqlwatch_volume_id]
		and h.last_snapshot_time = lg.snapshot_time
		
), cte_volume_growth as (
	select [sql_instance], [sqlwatch_volume_id], [volume_name], [label], [file_system], [volume_block_size_bytes], [date_created], [date_last_seen]
	, [volume_total_space_bytes_current], [volume_free_space_bytes_current], volume_bytes_growth, [total_growth_days]

	, growth_bytes_per_day = case when [total_growth_days] > 0 and volume_bytes_growth > 0 then [volume_bytes_growth] / [total_growth_days] else 0 end
	, days_until_full = case when [total_growth_days] > 0 and volume_bytes_growth > 0 then volume_free_space_bytes_current / ([volume_bytes_growth] / [total_growth_days]) else 9999 end
	, [free_space_percentage]
	, [is_record_deleted]
	from cte_volume
	)
	select [sql_instance], [sqlwatch_volume_id], [volume_name], [label], [file_system], [volume_block_size_bytes], [date_created], [date_last_seen]
		, [volume_total_space_bytes_current], [volume_free_space_bytes_current], [volume_bytes_growth], [total_growth_days], [free_space_percentage]
		, [growth_bytes_per_day], [days_until_full], [is_record_deleted]
	, [total_space_formatted] = [dbo].[ufn_sqlwatch_format_bytes] ( volume_total_space_bytes_current )
	, [free_space_formatted] = [dbo].[ufn_sqlwatch_format_bytes] ( [volume_free_space_bytes_current] )
	, [growth_bytes_per_day_formatted] = [dbo].[ufn_sqlwatch_format_bytes] ( growth_bytes_per_day ) + ' /Day'
	, [free_space_percentage_formatted] = convert(varchar(50),convert(decimal(10,0),volume_free_space_bytes_current * 1.0 / volume_total_space_bytes_current  * 100.0)) + ' %'
	from cte_volume_growth
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_logger_disk_utilisation_table](
	[sqlwatch_database_id] [smallint] NOT NULL,
	[sqlwatch_table_id] [int] NOT NULL,
	[row_count] [real] NOT NULL,
	[total_pages] [real] NOT NULL,
	[used_pages] [real] NOT NULL,
	[data_compression] [tinyint] NULL,
	[snapshot_type_id] [tinyint] NOT NULL,
	[snapshot_time] [datetime2](0) NOT NULL,
	[sql_instance] [varchar](32) NOT NULL,
	[row_count_delta] [real] NULL,
	[total_pages_delta] [real] NULL,
	[used_pages_delta] [real] NULL,
 CONSTRAINT [pk_sqlwatch_logger_disk_utilisation_table] PRIMARY KEY CLUSTERED 
(
	[snapshot_time] ASC,
	[sql_instance] ASC,
	[snapshot_type_id] ASC,
	[sqlwatch_database_id] ASC,
	[sqlwatch_table_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, DATA_COMPRESSION = PAGE) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_meta_table](
	[sql_instance] [varchar](32) NOT NULL,
	[sqlwatch_database_id] [smallint] NOT NULL,
	[sqlwatch_table_id] [int] IDENTITY(1,1) NOT NULL,
	[table_name] [nvarchar](512) NULL,
	[table_type] [nvarchar](128) NULL,
	[date_first_seen] [datetime] NOT NULL,
	[date_last_seen] [datetime] NOT NULL,
 CONSTRAINT [pk_sqlwatch_meta_database_table] PRIMARY KEY CLUSTERED 
(
	[sql_instance] ASC,
	[sqlwatch_database_id] ASC,
	[sqlwatch_table_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY],
 CONSTRAINT [uq_sqlwatch_meta_table_table] UNIQUE NONCLUSTERED 
(
	[sql_instance] ASC,
	[sqlwatch_database_id] ASC,
	[table_name] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE VIEW [dbo].[vw_sqlwatch_report_dim_table] with schemabinding
	AS 

	with cte_table as (
		select t.[sql_instance], t.[sqlwatch_database_id], t.[sqlwatch_table_id], [table_name], [table_type], [date_first_seen], t.[date_last_seen]
		, used_pages_growth = lg.used_pages  - fg.used_pages
		, row_count_growth = lg.[row_count] - fg.[row_count]
		, total_growth_days = datediff(day,fg.snapshot_time,lg.snapshot_time)
		, last_snapshot_time=lg.snapshot_time
		, used_pages_current = lg.used_pages
		, row_count_current = lg.row_count
		, db.[database_name]
		, lg.data_compression
		from dbo.sqlwatch_meta_table t

		inner join dbo.sqlwatch_meta_database db
			on db.sqlwatch_database_id = t.sqlwatch_database_id
			and db.sql_instance = t.sql_instance
			and db.is_current = 1

		-- get first and last snapshot dates
		left join (
			select [sql_instance], sqlwatch_database_id, sqlwatch_table_id
					, first_snapshot_time=min(snapshot_time)
					, last_snapshot_time=max(snapshot_time) 
			from [dbo].[sqlwatch_logger_disk_utilisation_table] 
			group by [sql_instance], sqlwatch_database_id, sqlwatch_table_id
		) h
		on h.sql_instance = t.sql_instance
		and h.sqlwatch_database_id = t.sqlwatch_database_id
		and h.sqlwatch_table_id = t.sqlwatch_table_id

		-- get first snapshot data
		left join [dbo].[sqlwatch_logger_disk_utilisation_table]  fg
		on fg.sql_instance = h.sql_instance
		and fg.sqlwatch_database_id = h.sqlwatch_database_id
		and fg.sqlwatch_table_id = h.sqlwatch_table_id
		and fg.snapshot_time = h.first_snapshot_time

		-- get last snapshot data
		left join [dbo].[sqlwatch_logger_disk_utilisation_table]  lg
		on lg.sql_instance = h.sql_instance
		and lg.sqlwatch_database_id = h.sqlwatch_database_id
		and lg.sqlwatch_table_id = h.sqlwatch_table_id
		and lg.snapshot_time = h.last_snapshot_time
), 
	cte_table_growth as (
			select [sql_instance], [sqlwatch_database_id], [sqlwatch_table_id], [table_name], [table_type]
			, [date_first_seen], [date_last_seen], [total_growth_days]
			, [used_pages_growth_per_day] = case when [total_growth_days] > 0 and used_pages_growth > 0 then used_pages_growth / total_growth_days else 0 end
			, [row_count_growth_per_day] = case when [total_growth_days] > 0 and row_count_growth > 0 then row_count_growth / total_growth_days else 0 end
			, last_snapshot_time,used_pages_current,row_count_current, [database_name], data_compression
			from cte_table
		)
		select 
			  [sql_instance]
			, [sqlwatch_database_id]
			, [database_name]
			, [sqlwatch_table_id]
			, [table_name]
			, [table_type]
			, [date_first_seen]
			, [date_last_seen]
			, used_pages_current 
			/* 1 page is 8KB but the function expects bytes */
			, used_bytes_current_formatted = [dbo].[ufn_sqlwatch_format_bytes] (used_pages_current * 1024.00 * 8.00)
			, growth_bytes_per_day_formatted = [dbo].[ufn_sqlwatch_format_bytes] ([used_pages_growth_per_day] * 1024.00 * 8.00) + ' / Day'

			, row_count_current
			, [row_count_growth_per_day]
			, [used_pages_growth_per_day]
			, last_snapshot_time
			, data_compression = case data_compression
when 0 then 'NONE'
when 1 then 'ROW'
when 2 then 'PAGE'
when 3 then 'COLUMNSTORE : Applies to: SQL Server 2012 (11.x) and later'
when 4 then 'COLUMNSTORE_ARCHIVE : Applies to: SQL Server 2014 (12.x) and later'
else 'UNKNOWN' end
		from cte_table_growth
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE VIEW [dbo].[vw_sqlwatch_report_fact_perf_file_stats] WITH SCHEMABINDING
AS

select [sqlwatch_database_id], [sqlwatch_master_file_id]

, f.[database_name]
, f.[file_name]

, f.file_type
, file_type_desc = [dbo].[ufn_sqlwatch_get_file_type_desc](f.file_type)
, f.file_physical_name

, [num_of_reads], [num_of_bytes_read], [io_stall_read_ms], [num_of_writes], [num_of_bytes_written]
, [io_stall_write_ms], [size_on_disk_bytes], report_time, d.[sql_instance], [num_of_reads_delta], [num_of_bytes_read_delta]
, [io_stall_read_ms_delta], [num_of_writes_delta], [num_of_bytes_written_delta], [io_stall_write_ms_delta], [size_on_disk_bytes_delta], [delta_seconds]
, io_latency_read = case when num_of_reads_delta > 0 then [io_stall_read_ms_delta] / num_of_reads_delta else 0 end
, io_latency_write = case when [num_of_writes_delta] > 0 then [io_stall_write_ms_delta] / [num_of_writes_delta] else 0 end
, [bytes_written_per_second] = case when isnull([delta_seconds],0) > 0 then [num_of_bytes_written_delta] / [delta_seconds] else 0 end
, [bytes_read_per_second] = case when isnull([delta_seconds],0) > 0 then [num_of_bytes_read_delta] / [delta_seconds] else 0 end
 --for backward compatibility with existing pbi, this column will become report_time as we could be aggregating many snapshots in a report_period
, d.snapshot_time
, d.snapshot_type_id
	from [dbo].[sqlwatch_logger_perf_file_stats] d

  	inner join dbo.sqlwatch_logger_snapshot_header h
		on  h.snapshot_time = d.[snapshot_time]
		and h.snapshot_type_id = d.snapshot_type_id
		and h.sql_instance = d.sql_instance

	/*  using outer apply instead of inner join is SOO MUCH slower...
		BUT it only applies to the columns we select.
		If we do not select any columns from the outer apply, it does not get applied whereas joins
		always do whether we select columns or not. 99% of the time these views will feed PowerBI wher only IDs are required
		and small subset of columns queried. that 1% will be DBAs querying views directly in SSMS (TOP (1000)) in which case, 
		having actual names instead alongisde IDs will make their life easier with small increase in performane penalty */
	outer apply (
		select file_physical_name, [file_name], file_type, mdb.[database_name]
		from dbo.sqlwatch_meta_master_file mf
		inner join [dbo].[sqlwatch_meta_database] mdb
			on mdb.sql_instance = mf.sql_instance
			and mdb.sqlwatch_database_id = mf.sqlwatch_database_id
		where mf.sqlwatch_master_file_id = d.sqlwatch_master_file_id
		and mf.sqlwatch_database_id = d.sqlwatch_database_id
		and mf.sql_instance = d.sql_instance
		) f
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE FUNCTION [dbo].[ufn_sqlwatch_get_servername]()

RETURNS varchar(32) with schemabinding
AS
BEGIN
	-- this has two purposes.
	-- first we do explicit conversion in a single place
	-- second, we can manipulate the @@SERVERNAME, handy on the managed instances where server names can be 255 char long.
	RETURN convert(varchar(32),@@SERVERNAME)
END
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE VIEW [dbo].[vw_sqlwatch_sys_databases]
as

select 
	  sql_instance = dbo.ufn_sqlwatch_get_servername()
	, [d].[name]
	, [d].[database_id]
	, [d].[create_date]
	, [d].[is_auto_close_on]
	, [d].[is_auto_shrink_on]
	, [d].[is_auto_update_stats_on]
	, [d].[user_access]
	, [d].[state]
	, [d].[snapshot_isolation_state] 
	, [d].[is_read_committed_snapshot_on] 
	, [d].[recovery_model] 
	, [d].[page_verify_option] 
from sys.databases d

/*	remove these joins and related where clauses when building for SQL2008 */
/* https://github.com/marcingminski/sqlwatch/issues/108 */
left join sys.dm_hadr_availability_replica_states hars 
	on d.replica_id = hars.replica_id
left join sys.availability_replicas ar 
	on d.replica_id = ar.replica_id

where state_desc = 'ONLINE' --only online database

/* AG dbs */
and ( 
		--if part of AG include primary only
		(hars.role_desc = 'PRIMARY' OR hars.role_desc IS NULL)

		--OR if part of AG include secondary only when is readable
	or  (hars.role_desc = 'SECONDARY' AND ar.secondary_role_allow_connections_desc IN ('READ_ONLY','ALL'))
)
and source_database_id is null --exclude snapshots
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE FUNCTION [dbo].[ufn_sqlwatch_get_server_utc_offset](
	@time_unit VARCHAR(20) = 'MINUTE'
)
RETURNS INT WITH SCHEMABINDING
AS
BEGIN
	--hour	hh
	--minute	mi, n

	RETURN (
		SELECT CASE 
			WHEN UPPER(@time_unit) in ('N','MI','MINUTE') THEN DATEDIFF(MINUTE,DATEADD(SECOND, DATEDIFF(SECOND, GETDATE(), GETUTCDATE()), GETDATE()),GETDATE())
			WHEN UPPER(@time_unit) in ('HOUR','HH') THEN DATEDIFF(HOUR,DATEADD(SECOND, DATEDIFF(SECOND, GETDATE(), GETUTCDATE()), GETDATE()),GETDATE())
			ELSE NULL END
	)
END
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE FUNCTION [dbo].[ufn_sqlwatch_convert_time_utc]
(
	@local_time datetime
)
RETURNS datetime WITH SCHEMABINDING
AS
BEGIN
	-- the dbo.ufn_sqlwatch_get_server_utc_offset() gives the offset from the UTC time to local time.
	-- if we want to go back from local to UTC, we have to substract it.
	-- for time zones behind UTC it will be a double negative which turns into a positive
	RETURN dateadd(Hour,dbo.ufn_sqlwatch_get_server_utc_offset('HOUR')*-1,@local_time)
END
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE FUNCTION [dbo].[ufn_sqlwatch_get_check_status]
(
	@threshold varchar(100),
	@value decimal(28,5),
	@variance_percent smallint
)
RETURNS bit with schemabinding
AS
BEGIN
	--1: MATCH, 0: NOT MATCH
	declare @variance_percent_dec_max decimal(10,2) = (1+(@variance_percent * 1.0 / 100)),
			@variance_percent_dec_min decimal(10,2) = (1-(@variance_percent * 1.0 / 100)),
			@return bit = 0,
			@threshold_value decimal(28,5) = [dbo].[ufn_sqlwatch_get_threshold_value](@threshold),
			@threshold_comparator varchar(2) = [dbo].[ufn_sqlwatch_get_threshold_comparator](@threshold)

	if @threshold_comparator = '<='
		begin
			if @value  <= @threshold_value * @variance_percent_dec_min
				begin
					set @return = 1
				end
		end
	else if @threshold_comparator = '>='
		begin
			if @value >= @threshold_value * @variance_percent_dec_max
				begin
					set @return = 1
				end
		end
	else if @threshold_comparator = '<>'
		begin
			if @value <> @threshold_value
				begin
					set @return = 1
				end
		end
	else if @threshold_comparator = '<'
		begin
			if @value < @threshold_value * @variance_percent_dec_min
				begin
					set @return = 1
				end
		end
	else if @threshold_comparator = '>'
		begin
			if @value > @threshold_value * @variance_percent_dec_max
				begin
					set @return = 1
				end
		end
	else if @threshold_comparator = '='
		begin
			if @value = @threshold_value
				begin
					set @return = 1
				end
		end
	else if @value = @threshold_value
		begin
				begin
					set @return = 1
				end
		end
	else
		begin
			set @return = 0
		end

	return @return
END
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE FUNCTION [dbo].[ufn_sqlwatch_get_threshold_deviation]
(
	@threshold varchar(50),
	@variance smallint
)
RETURNS decimal(28,5) with schemabinding
AS
BEGIN
	declare @return decimal(28,5),
			@threshold_value decimal(28,5) = [dbo].[ufn_sqlwatch_get_threshold_value](@threshold),
			@threshold_comparator varchar(2) = [dbo].[ufn_sqlwatch_get_threshold_comparator](@threshold);

	if left(@threshold_comparator,1) = '<' 
		begin
			set @return = case when @threshold_value = 0 then ( 0 - ( @variance * 1.0 / 100 ) ) else @threshold_value * ( 1 - ( @variance * 1.0 / 100 ) ) end
		end
	else if left(@threshold_comparator,1) = '>'
		begin
			set @return = case when @threshold_value = 0 then ( 0 + ( @variance * 1.0 / 100 ) ) else @threshold_value * ( 1 + ( @variance * 1.0 / 100 ) ) end 
		end
	else
		--anything else such as = or <> will not no variance
		begin
			set @return = @threshold_value
		end


	return @return;
END
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_logger_perf_procedure_stats](
	[sql_instance] [varchar](32) NOT NULL,
	[sqlwatch_database_id] [smallint] NOT NULL,
	[sqlwatch_procedure_id] [int] NOT NULL,
	[snapshot_time] [datetime2](0) NOT NULL,
	[snapshot_type_id] [tinyint] NOT NULL,
	[cached_time] [datetime] NOT NULL,
	[last_execution_time] [datetime] NOT NULL,
	[execution_count] [real] NULL,
	[total_worker_time] [real] NULL,
	[last_worker_time] [real] NULL,
	[min_worker_time] [real] NULL,
	[max_worker_time] [real] NULL,
	[total_physical_reads] [real] NULL,
	[last_physical_reads] [real] NULL,
	[min_physical_reads] [real] NULL,
	[max_physical_reads] [real] NULL,
	[total_logical_writes] [real] NULL,
	[last_logical_writes] [real] NULL,
	[min_logical_writes] [real] NULL,
	[max_logical_writes] [real] NULL,
	[total_logical_reads] [real] NULL,
	[last_logical_reads] [real] NULL,
	[min_logical_reads] [real] NULL,
	[max_logical_reads] [real] NULL,
	[total_elapsed_time] [real] NULL,
	[last_elapsed_time] [real] NULL,
	[min_elapsed_time] [real] NULL,
	[max_elapsed_time] [real] NULL,
	[delta_worker_time] [real] NULL,
	[delta_physical_reads] [real] NULL,
	[delta_logical_writes] [real] NULL,
	[delta_logical_reads] [real] NULL,
	[delta_elapsed_time] [real] NULL,
	[delta_execution_count] [real] NULL,
 CONSTRAINT [pk_sqlwatch_logger_perf_procedure_stats] PRIMARY KEY CLUSTERED 
(
	[sql_instance] ASC,
	[sqlwatch_database_id] ASC,
	[sqlwatch_procedure_id] ASC,
	[snapshot_time] ASC,
	[snapshot_type_id] ASC,
	[cached_time] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, DATA_COMPRESSION = PAGE) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_meta_procedure](
	[sqlwatch_procedure_id] [int] IDENTITY(1,1) NOT NULL,
	[sql_instance] [varchar](32) NOT NULL,
	[sqlwatch_database_id] [smallint] NOT NULL,
	[procedure_name] [nvarchar](256) NOT NULL,
	[procedure_type] [char](1) NOT NULL,
	[date_first_seen] [datetime] NOT NULL,
	[date_last_seen] [datetime] NOT NULL,
 CONSTRAINT [pk_sqlwatch_meta_procedure] PRIMARY KEY CLUSTERED 
(
	[sql_instance] ASC,
	[sqlwatch_database_id] ASC,
	[sqlwatch_procedure_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE VIEW [dbo].[vw_sqlwatch_report_fact_perf_procedure_stats]
with schemabinding
as

select 
	  [ps].[sql_instance]
	, [ps].[sqlwatch_database_id]
	, [ps].[sqlwatch_procedure_id]
	, [ps].[snapshot_time]
	, [ps].[snapshot_type_id]
	, [ps].[cached_time]
	, [ps].[last_execution_time]
	, [ps].[execution_count]
	, [ps].[total_worker_time]
	, [ps].[last_worker_time]
	, [ps].[min_worker_time]
	, [ps].[max_worker_time]
	, [ps].[total_physical_reads]
	, [ps].[last_physical_reads]
	, [ps].[min_physical_reads]
	, [ps].[max_physical_reads]
	, [ps].[total_logical_writes]
	, [ps].[last_logical_writes]
	, [ps].[min_logical_writes]
	, [ps].[max_logical_writes]
	, [ps].[total_logical_reads]
	, [ps].[last_logical_reads]
	, [ps].[min_logical_reads]
	, [ps].[max_logical_reads]
	, [ps].[total_elapsed_time]
	, [ps].[last_elapsed_time]
	, [ps].[min_elapsed_time]
	, [ps].[max_elapsed_time]
	, [ps].[delta_worker_time]
	, [ps].[delta_physical_reads]
	, [ps].[delta_logical_writes]
	, [ps].[delta_logical_reads]
	, [ps].[delta_elapsed_time]
	, [ps].[delta_execution_count]
	, [d].[database_name]
	, [p].[procedure_name]
	, [last_execution_time_utc]=[dbo].[ufn_sqlwatch_convert_time_utc]([last_execution_time])
	, [cached_time_utc]=[dbo].[ufn_sqlwatch_convert_time_utc]([cached_time])
	, [cpu_time] = delta_worker_time*1.0/delta_execution_count
	, [physical_reads] = [delta_physical_reads]*1.0/delta_execution_count
	, [logical_reads] = [delta_logical_reads]*1.0/delta_execution_count
	, [logical_writes] = [delta_logical_writes]*1.0/delta_execution_count
	, [elapsed_time] = delta_elapsed_time/delta_execution_count

from [dbo].[sqlwatch_logger_perf_procedure_stats] ps

inner join [dbo].[sqlwatch_meta_procedure] p
	on p.sqlwatch_procedure_id = ps.sqlwatch_procedure_id
	and p.sqlwatch_database_id = ps.sqlwatch_database_id
	and p.sql_instance = ps.sql_instance

inner join [dbo].[sqlwatch_meta_database] d 
	on p.sql_instance = d.sql_instance 
	and p.sqlwatch_database_id = d.sqlwatch_database_id
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE FUNCTION [dbo].[ufn_sqlwatch_time_intervals]
(
	@snapshot_type_id tinyint = null,
	@interval_minutes smallint = null,
	@report_window int = 4,
	@report_end_time datetime = null
	/* for the function to assign default value to an input parametr, the input must be set to DEFAULT, not NULL i.e.
	   select * from [dbo].[ufn_sqlwatch_time_intervals](DEFAULT,DEFAULT,DEFAULT,DEFAULT)
	   instead of
	   select * from [dbo].[ufn_sqlwatch_time_intervals](NULL,NULL,NULL,NULL)
	   to add some flexibility when calling it from stored procedures, I am going to handle null values explicitly */
)
RETURNS TABLE
AS RETURN (
	/* if no @interval_minutes parameter specified we are going to 
		pick best interval based on report window here */
	with cte_interval_window as (
		select interval_minutes = case when @interval_minutes is null then
			case
				when @report_window <= 1 then 2
				when @report_window <= 4 then 5
				when @report_window is null then 5 -- default value for report window is 4 hours which would have given 5 minute interval
				when @report_window <= 24 then 15
				when @report_window <= 168 then 60
				when @report_window <= 720 then 360
			else 1440 end
		else @interval_minutes end
	)
		select
				[first_snapshot_time]	= min([snapshot_time])
			 ,  [last_snapshot_time]	= max([snapshot_time])
			 ,	[spapshot_interval_start]	= convert(datetime,dateadd(mi,(datediff(mi,0, [snapshot_time])/ interval_minutes) * interval_minutes,0))
			 ,	[snapshot_interval_end]		= dateadd(mi, interval_minutes, convert(datetime,dateadd(mi,(datediff(mi,0, [snapshot_time])/ interval_minutes) * interval_minutes,0)))
			 ,	[report_time_interval_minutes] = interval_minutes
			 ,	[snapshot_type_id]
			 ,	[snapshot_age_hours]	= datediff(hour,dateadd(mi, interval_minutes, convert(datetime,dateadd(mi,(datediff(mi,0, [snapshot_time])/ interval_minutes) * interval_minutes,0))),getutcdate())
			 ,  [sql_instance]
			 ,  [snapshot_collection_sequence]  = row_number() over (partition by [sql_instance], [snapshot_type_id] order by min([snapshot_time]))
		from [dbo].[sqlwatch_logger_snapshot_header]
		cross apply cte_interval_window
		where snapshot_type_id = isnull(@snapshot_type_id,snapshot_type_id)
		--set default report window to 4
		and snapshot_time >= DATEADD(HOUR, -isnull(@report_window,4), isnull(@report_end_time,getutcdate()))
		and snapshot_time <= isnull(@report_end_time,getutcdate())
		group by 
				convert(datetime,dateadd(mi,(datediff(mi,0, [snapshot_time])/ interval_minutes) * interval_minutes,0))
			,	dateadd(mi, interval_minutes, convert(datetime,dateadd(mi,(datediff(mi,0, [snapshot_time])/ interval_minutes) * interval_minutes,0)))
			,	interval_minutes
			,	[snapshot_type_id]
			,   [sql_instance]
)
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_logger_xes_blockers](
	[monitor_loop] [bigint] NOT NULL,
	[event_time] [datetime] NOT NULL,
	[blocked_ecid] [int] NOT NULL,
	[blocked_spid] [int] NOT NULL,
	[blocking_ecid] [int] NOT NULL,
	[blocking_spid] [int] NOT NULL,
	[report_xml] [xml] NOT NULL,
	[lockMode] [nvarchar](128) NULL,
	[blocked_clientapp] [nvarchar](128) NULL,
	[blocked_currentdbname] [nvarchar](128) NULL,
	[blocked_hostname] [nvarchar](128) NULL,
	[blocked_loginname] [nvarchar](128) NULL,
	[blocked_inputbuff] [nvarchar](max) NULL,
	[blocking_clientapp] [nvarchar](128) NULL,
	[blocking_currentdbname] [nvarchar](128) NULL,
	[blocking_hostname] [nvarchar](128) NULL,
	[blocking_loginname] [nvarchar](128) NULL,
	[blocking_inputbuff] [varchar](max) NULL,
	[blocking_duration_ms] [real] NULL,
	[snapshot_time] [datetime2](0) NOT NULL,
	[snapshot_type_id] [tinyint] NOT NULL,
	[sql_instance] [varchar](32) NOT NULL,
 CONSTRAINT [pk_logger_perf_xes_blockers] PRIMARY KEY CLUSTERED 
(
	[event_time] ASC,
	[monitor_loop] ASC,
	[blocked_spid] ASC,
	[blocked_ecid] ASC,
	[blocking_spid] ASC,
	[blocking_ecid] ASC,
	[sql_instance] ASC,
	[snapshot_time] ASC,
	[snapshot_type_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, DATA_COMPRESSION = PAGE) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE FUNCTION [dbo].[ufn_sqlwatch_get_blocking_chains] 
(
	@start_date datetime2(0),
	@end_date datetime2(0),
	@sql_instance varchar(32) = null
) 

RETURNS @returntable TABLE 
(
	[monitor_loop] [bigint] NULL,
	[event_time] [datetime] NULL,
	[blocking_tree] [nvarchar](4000) NULL,
	[blocking_level] [int] NULL,
	[session_id] [int] NULL,
	[blocking_session_id] [int] NULL,
	[database name] [nvarchar](128) NULL,
	[lock_mode] [nvarchar](128) NULL,
	[blocking_duration_ms] [real] NULL,
	[appname] [nvarchar](128) NULL,
	[hostname] [nvarchar](128) NULL,
	[sql_text] [nvarchar](max) NULL,
	[report_xml] [xml] NULL,
	[sequence] [bigint] NULL,
	sql_instance varchar(32),
	snapshot_time datetime2(0),
	snapshot_type_id tinyint
) with schemabinding
AS
BEGIN

		if @sql_instance is null
			begin
				set @sql_instance= dbo.ufn_sqlwatch_get_servername();
			end;

		with cte_block_headers AS
		(
			select 
				  session_id = blocking_spid
				, ecid = blocking_ecid
				, monitor_loop
			from [dbo].[sqlwatch_logger_xes_blockers]

			--this is a chance, that we will select a subset of the original chain here and will miss the head blocker.
			--ideally, we'd need to select all rows participating in a blocking chain that had at least one event between these dates.
			where event_time between @start_date and @end_date
			and sql_instance = @sql_instance

			except
			
			select 
				  session_id = blocked_spid
				, ecid = blocked_ecid
				, monitor_loop
			from [dbo].[sqlwatch_logger_xes_blockers]
			where event_time between @start_date and @end_date
			and sql_instance = @sql_instance
		), 


		cte_blocking_hierarchy AS
		(
			--blockers
			select
					monitor_loop
				,	session_id
				,	ecid
				,	blocking_chain = cast('/' + CAST(session_id as varchar(20)) + '.' + CAST(ecid as varchar(20)) + '/' as varchar(max))
				,	blocking_spid = 0 
				,	blocking_ecid = 0
				,	blocking_level = 0 
				,	blocking_level_t = cast (replicate ( '0', 4 - len(cast(session_id as varchar(10)))) + cast (session_id as varchar(10)) as varchar(max))
			from cte_block_headers
	
			union all
	
			--blocked
			select 
					h.monitor_loop
				,	b.blocked_spid
				,	b.blocked_ecid
				,	cast(h.blocking_chain + CAST(b.blocked_spid as varchar(20)) + '.' + CAST(b.blocked_ecid as varchar(20)) + '/' as varchar(max))
				,	b.blocking_spid
				,	b.blocking_ecid
				,	h.blocking_level+1
				,	blocking_level_t = cast (h.blocking_level_t + right (cast ((1000 + h.session_id) as varchar(100)), 4) as varchar (max))
			from [dbo].[sqlwatch_logger_xes_blockers] b
			join cte_blocking_hierarchy h
				on b.monitor_loop = h.monitor_loop
				and b.blocking_spid = h.session_id
				and b.blocking_ecid = h.ecid
			where b.event_time between @start_date and @end_date
			and sql_instance = @sql_instance
		)
		
		INSERT @returntable
		select 
			  h.monitor_loop
			, event_time = case when h.blocking_level = 0 then bhead.event_time else bproc.event_time end

			--the visual tree inspired by https://blog.sqlauthority.com/2015/07/07/sql-server-identifying-blocking-chain-using-sql-scripts/
			, blocking_tree = N'    ' + char (160) + char (160) + replicate (N'|         ', len (blocking_level_t)/4 - 1) +
			  case when (len(blocking_level_t)/4 - 1) = 0
			  then 'HEAD BLOCKER -  '
			  else '|------  ' end
			  + 'SPID '+ case when session_id <=50 then '(SYSTEM)' else '' end + ': ' + cast ( session_id as nvarchar(10))
			, h.blocking_level
			, session_id = h.session_id
			, blocking_session_id = nullif(h.blocking_spid ,0)
			, [database name] = case when bproc.blocking_spid is not null then bproc.[blocked_currentdbname] else bhead.[blocking_currentdbname] end -- isnull(,bht.[database name])
			, [lock_mode] = isnull(bproc.[lockMode], bhead.[lockMode])
			, [blocking_duration_ms] = isnull(bproc.[blocking_duration_ms], bhead.[blocking_duration_ms])
			, [appname]= case when h.blocking_level = 0 then bhead.blocking_clientapp else bproc.blocked_clientapp end
			, [hostname]= case when h.blocking_level = 0 then bhead.[blocking_hostname] else bproc.[blocked_hostname] end
			, [sql_text]= case when h.blocking_level = 0 then bhead.blocking_inputbuff else bproc.[blocked_inputbuff] end
			, report_xml = isnull(bproc.report_xml,bhead.report_xml)
			, sequence = ROW_NUMBER() over (order by h.monitor_loop , h.blocking_chain)
			, sql_instance = @sql_instance
			, snapshot_time = isnull(bproc.snapshot_time,bhead.snapshot_time)
			, snapshot_type_id = isnull(bproc.snapshot_type_id,bhead.snapshot_type_id)
		from cte_blocking_hierarchy h

		--block process details
		left join [dbo].[sqlwatch_logger_xes_blockers] bproc 
			on bproc.monitor_loop = h.monitor_loop
			and bproc.blocked_spid = h.session_id
			and bproc.blocked_ecid = h.ecid
			and bproc.event_time between @start_date and @end_date
			and sql_instance = @sql_instance

		--blocked header details
		outer apply (
			select top 1 
				  [monitor_loop]
				, [event_time]
				, [blocked_ecid]
				, [blocked_spid]
				, [blocking_ecid]
				, [blocking_spid]
				, [report_xml]
				, [lockMode]
				, [blocked_clientapp]
				, [blocked_currentdbname]
				, [blocked_hostname]
				, [blocked_loginname]
				, [blocked_inputbuff]
				, [blocking_clientapp]
				, [blocking_currentdbname]
				, [blocking_hostname]
				, [blocking_loginname]
				, [blocking_inputbuff]
				, [blocking_duration_ms]
				, [snapshot_time]
				, [snapshot_type_id]
				, [sql_instance] 
			from [dbo].[sqlwatch_logger_xes_blockers] bheadt 
			where bheadt.monitor_loop = h.monitor_loop
			and bheadt.blocking_spid = h.session_id
			and bheadt.blocking_ecid = h.ecid
			and h.blocking_level=0
			and bheadt.event_time between @start_date and @end_date
			and bheadt.sql_instance = @sql_instance


		) bhead
	RETURN
END
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE FUNCTION [dbo].[ufn_sqlwatch_clean_sql_text] 
(
	@sql_text varchar(max)
) 
RETURNS varchar(max) with schemabinding
AS
BEGIN
	RETURN (
		replace(replace(replace(replace(replace(@sql_text,char(9), ''),'','') ,' ',char(9)+char(10)),char(10)+char(9),''),char(9)+char(10),' ')
	);
END;
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE FUNCTION [dbo].[ufn_sqlwatch_parse_xes_event_data](@event_data xml) 
RETURNS @retEventData TABLE
(
	duration int,
	cpu_time int,
	physical_reads int,
	logical_reads int,
	writes int,
	row_count int,
	last_row_count int,
	line_number int,
	offset int,
	offset_end int,
	sql_text varchar(max),
	client_app_name varchar(max),
	client_hostname varchar(max),
	database_name varchar(max),
	plan_handle varchar(max),
	session_id varchar(max),
	username varchar(max)

) with schemabinding
AS
BEGIN
	insert @retEventData
	select 
		@event_data.value('(event/data[@name="duration"]/value)[1]', 'int') as Duration,
		@event_data.value('(event/data[@name="cpu_time"]/value)[1]', 'int') as cpu_time,
		@event_data.value('(event/data[@name="physical_reads"]/value)[1]', 'int') as physical_reads,
		@event_data.value('(event/data[@name="logical_reads"]/value)[1]', 'int') as logical_reads,
		@event_data.value('(event/data[@name="writes"]/value)[1]', 'int') as writes,
		@event_data.value('(event/data[@name="row_count"]/value)[1]', 'int') as row_count,
		@event_data.value('(event/data[@name="last_row_count"]/value)[1]', 'int') as last_row_count,
		@event_data.value('(event/data[@name="line_number"]/value)[1]', 'int') as line_number,
		@event_data.value('(event/data[@name="offset"]/value)[1]', 'int') as offset,
		@event_data.value('(event/data[@name="offset_end"]/value)[1]', 'int') as offset_end,
		dbo.ufn_sqlwatch_clean_sql_text(@event_data.value('(event/action[@name="sql_text"]/value)[1]', 'varchar(max)')) as sql_text,
		@event_data.value('(event/action[@name="client_app_name"]/value)[1]', 'varchar(max)') as client_app_name,
		@event_data.value('(event/action[@name="client_hostname"]/value)[1]', 'varchar(max)') as client_hostname,
		@event_data.value('(event/action[@name="database_name"]/value)[1]', 'varchar(max)') as [database_name],
		convert(varbinary(64),'0x' + @event_data.value('(action[@name="plan_handle"]/value)[1]', 'varchar(max)'),1) as plan_handle,
		@event_data.value('(event/action[@name="session_id"]/value)[1]', 'int') as session_id,
		@event_data.value('(event/action[@name="username"]/value)[1]', 'varchar(max)') as username;

	RETURN;
END;
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE VIEW [dbo].[vw_sqlwatch_report_fact_xes_blockers] with schemabinding
as

-- for backward compatibility only:
select 
	  [monitor_loop]
	, [event_time]
	, [blocking_tree]
	, [blocking_level]
	, [session_id]
	, [blocking_session_id]
	, [database name]
	, [lock_mode]
	, [blocking_duration_ms]
	, [appname]
	, [hostname]
	, [sql_text]
	, [report_xml]
	, [sequence]
	, sql_instance
	, snapshot_time
	, snapshot_type_id
from [dbo].[ufn_sqlwatch_get_blocking_chains]('1970-01-01','2099-12-31', null);
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_logger_xes_long_queries](
	[long_query_id] [bigint] IDENTITY(1,1) NOT NULL,
	[event_time] [datetime] NOT NULL,
	[event_name] [varchar](255) NOT NULL,
	[session_id] [bigint] NOT NULL,
	[sqlwatch_database_id] [smallint] NOT NULL,
	[cpu_time] [bigint] NULL,
	[physical_reads] [bigint] NULL,
	[logical_reads] [bigint] NULL,
	[writes] [bigint] NULL,
	[spills] [bigint] NULL,
	[username] [varchar](255) NULL,
	[client_hostname] [varchar](255) NULL,
	[client_app_name] [varchar](255) NULL,
	[duration_ms] [bigint] NULL,
	[snapshot_time] [datetime2](0) NOT NULL,
	[snapshot_type_id] [tinyint] NOT NULL,
	[sql_instance] [varchar](32) NOT NULL,
	[plan_handle] [varbinary](64) NOT NULL,
	[statement_start_offset] [int] NOT NULL,
	[statement_end_offset] [int] NOT NULL,
	[attach_activity_id] [varchar](40) NULL,
	[event_data] [xml] NULL,
 CONSTRAINT [pk_logger_perf_xes_long_queries] PRIMARY KEY CLUSTERED 
(
	[snapshot_time] ASC,
	[snapshot_type_id] ASC,
	[event_time] ASC,
	[event_name] ASC,
	[session_id] ASC,
	[plan_handle] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, DATA_COMPRESSION = PAGE) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_meta_query_plan](
	[sql_instance] [varchar](32) NOT NULL,
	[plan_handle] [varbinary](64) NOT NULL,
	[statement_start_offset] [int] NOT NULL,
	[statement_end_offset] [int] NOT NULL,
	[sql_handle] [varbinary](64) NULL,
	[query_hash] [varbinary](8) NOT NULL,
	[query_plan_hash] [varbinary](8) NOT NULL,
	[sqlwatch_database_id] [smallint] NOT NULL,
	[sqlwatch_procedure_id] [int] NOT NULL,
	[query_plan_for_plan_handle] [nvarchar](max) NULL,
	[statement_for_plan_handle] [varchar](max) NULL,
	[date_first_seen] [datetime2](0) NOT NULL,
	[date_last_seen] [datetime2](0) NOT NULL,
 CONSTRAINT [pk_sqlwatch_meta_query_plan_handle] PRIMARY KEY CLUSTERED 
(
	[sql_instance] ASC,
	[plan_handle] ASC,
	[query_plan_hash] ASC,
	[statement_start_offset] ASC,
	[statement_end_offset] ASC,
	[sqlwatch_database_id] ASC,
	[sqlwatch_procedure_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_meta_query_plan_hash](
	[sql_instance] [varchar](32) NOT NULL,
	[query_plan_hash] [varbinary](8) NOT NULL,
	[query_plan_for_query_plan_hash] [nvarchar](max) NULL,
	[statement_start_offset] [int] NULL,
	[statement_end_offset] [int] NULL,
	[statement_for_query_plan_hash] [varchar](max) NULL,
	[date_first_seen] [datetime] NULL,
	[date_last_seen] [datetime] NULL,
 CONSTRAINT [pk_sqlwatch_meta_plan_handle] PRIMARY KEY CLUSTERED 
(
	[sql_instance] ASC,
	[query_plan_hash] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE VIEW [dbo].[vw_sqlwatch_report_fact_xes_long_queries] with schemabinding
as

SELECT
       d.event_time
      ,d.[event_name]
      ,d.[session_id]
      ,d.[cpu_time]
      ,d.[physical_reads]
      ,d.[logical_reads]
      ,d.[writes]
      ,d.[spills]
      ,d.[username]
      ,d.[client_hostname]
      ,d.[client_app_name]
      ,d.[duration_ms]
      ,report_time
      ,d.[sql_instance]
      ,[long_query_id]
	  --for backward compatibility with existing pbi, this column will become report_time as we could be aggregating many snapshots in a report_period
	  , d.snapshot_time
	  , d.snapshot_type_id
    , qph.[query_plan_for_query_plan_hash]
	  , db.[database_name]
	  , pr.[procedure_name]
	  , coalesce(dbo.ufn_sqlwatch_clean_sql_text(qph.statement_for_query_plan_hash),ed.sql_text)
		  as sql_text
  FROM [dbo].[sqlwatch_logger_xes_long_queries] d
  	inner join dbo.sqlwatch_logger_snapshot_header h
		on  h.snapshot_time = d.[snapshot_time]
		and h.snapshot_type_id = d.snapshot_type_id
		and h.sql_instance = d.sql_instance

    left join dbo.[sqlwatch_meta_query_plan] qp
        on qp.sql_instance = d.sql_instance
        and qp.plan_handle = d.plan_handle
        and qp.statement_start_offset = d.statement_start_offset
        and qp.statement_end_offset = d.statement_end_offset

    left join dbo.[sqlwatch_meta_query_plan_hash] qph
        on qph.sql_instance = qp.sql_instance
        and qph.query_plan_hash = qp.query_plan_hash

    left join dbo.[sqlwatch_meta_database] db 
        on db.sqlwatch_database_id = qp.sqlwatch_database_id
        and db.sql_instance = qp.sql_instance

    left join dbo.[sqlwatch_meta_procedure] pr
        on pr.sqlwatch_procedure_id = qp.sqlwatch_procedure_id
        and pr.sql_instance = qp.sql_instance

	cross apply dbo.ufn_sqlwatch_parse_xes_event_data([event_data]) ed;
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_logger_xes_wait_event](
	[event_time] [datetime] NOT NULL,
	[wait_type_id] [smallint] NOT NULL,
	[duration] [bigint] NOT NULL,
	[signal_duration] [bigint] NULL,
	[session_id] [int] NOT NULL,
	[username] [nvarchar](255) NULL,
	[client_hostname] [nvarchar](255) NULL,
	[client_app_name] [nvarchar](255) NULL,
	[plan_handle] [varbinary](64) NULL,
	[statement_start_offset] [int] NOT NULL,
	[statement_end_offset] [int] NOT NULL,
	[sql_instance] [varchar](32) NOT NULL,
	[snapshot_time] [datetime2](0) NOT NULL,
	[snapshot_type_id] [tinyint] NOT NULL,
	[activity_id] [varchar](40) NOT NULL,
	[event_data] [xml] NULL,
 CONSTRAINT [pk_sqlwatch_logger_xes_wait_stat_event] PRIMARY KEY CLUSTERED 
(
	[event_time] ASC,
	[wait_type_id] ASC,
	[session_id] ASC,
	[sql_instance] ASC,
	[snapshot_time] ASC,
	[snapshot_type_id] ASC,
	[activity_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, DATA_COMPRESSION = PAGE) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE VIEW [dbo].[vw_sqlwatch_report_fact_xes_wait_events] with schemabinding
as

select e.[event_time]
      ,ws.[wait_type]
      ,ws.wait_category
      ,e.[duration]
      ,e.[signal_duration]
      ,e.[session_id]
      ,e.[username]
      ,e.[client_hostname]
      ,e.[client_app_name]
      --,p.plan_handle
      ,e.[sql_instance]
      ,e.[snapshot_time]
      ,e.[snapshot_type_id]
      ,e.plan_handle
      ,e.statement_start_offset
      ,e.statement_end_offset
      ,qph.[query_plan_for_query_plan_hash]
      ,db.[database_name]
	  ,pr.[procedure_name]
	  ,sql_text =  coalesce(dbo.ufn_sqlwatch_clean_sql_text(qph.statement_for_query_plan_hash),ed.sql_text)
      ,qp.query_plan_hash
  from [dbo].[sqlwatch_logger_xes_wait_event] e

    inner join dbo.vw_sqlwatch_meta_wait_stats_category ws
	    on e.[wait_type_id] = ws.[wait_type_id]
	    and e.sql_instance = ws.sql_instance

    left join dbo.[sqlwatch_meta_query_plan] qp
        on qp.sql_instance = e.sql_instance
        and qp.plan_handle = e.plan_handle
        and qp.statement_start_offset = e.statement_start_offset
        and qp.statement_end_offset = e.statement_end_offset

    left join dbo.[sqlwatch_meta_query_plan_hash] qph
        on qph.sql_instance = qp.sql_instance
        and qph.query_plan_hash = qp.query_plan_hash
        
    left join dbo.[sqlwatch_meta_database] db 
        on db.sqlwatch_database_id = qp.sqlwatch_database_id
        and db.sql_instance = qp.sql_instance

    left join dbo.[sqlwatch_meta_procedure] pr
        on pr.sqlwatch_procedure_id = qp.sqlwatch_procedure_id
        and pr.sql_instance = qp.sql_instance
			
    cross apply dbo.ufn_sqlwatch_parse_xes_event_data([event_data]) ed;
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE VIEW [dbo].[vw_sqlwatch_app_version] with schemabinding
	AS 
select [install_sequence], [install_date], [sqlwatch_version]

,major = parsename([sqlwatch_version],4)
,minor = parsename([sqlwatch_version],3)
,patch = parsename([sqlwatch_version],2)
,build = parsename([sqlwatch_version],1)
from [dbo].[sqlwatch_app_version]
where [install_sequence] = (
	select max([install_sequence])
	from [dbo].[sqlwatch_app_version]
	)
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE VIEW [dbo].[vw_sqlwatch_help_action_queue_failures] with schemabinding
	AS select top 100 percent
		  [sql_instance]
		, [queue_item_id]
		, [action_exec_type]
		, [time_queued]
		, [action_exec]
		, [exec_status]
		, [exec_time_start]
		, [exec_time_end]
		, [retry_count]
	from [dbo].[sqlwatch_meta_action_queue]
	where exec_status <> 'OK'
	order by queue_item_id desc
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE VIEW [dbo].[vw_sqlwatch_help_logger_log_failures] with schemabinding
as
SELECT 
		 [event_sequence]
		,[sql_instance]
		,[event_time]
		,[process_name]
		,[process_stage]
		,[process_message]
		,[process_message_type]
		,[spid]
		,[process_login]
		,[process_user]
		,[ERROR_NUMBER]
		,[ERROR_SEVERITY]
		,[ERROR_STATE]
		,[ERROR_PROCEDURE]
		,[ERROR_LINE]
		,[ERROR_MESSAGE]
  FROM [dbo].[sqlwatch_app_log]
  where [process_message_type] = 'ERROR'
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_logger_errorlog](
	[sql_instance] [varchar](32) NOT NULL,
	[log_date] [datetime] NOT NULL,
	[attribute_id] [smallint] NOT NULL,
	[errorlog_text_id] [int] NOT NULL,
	[keyword_id] [smallint] NOT NULL,
	[log_type_id] [int] NOT NULL,
	[snapshot_time] [datetime2](0) NOT NULL,
	[snapshot_type_id] [tinyint] NOT NULL,
	[record_count] [real] NULL,
 CONSTRAINT [pk_sqlwatch_logger_errorlog] PRIMARY KEY CLUSTERED 
(
	[snapshot_time] ASC,
	[log_date] ASC,
	[attribute_id] ASC,
	[errorlog_text_id] ASC,
	[keyword_id] ASC,
	[log_type_id] ASC,
	[snapshot_type_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, DATA_COMPRESSION = PAGE) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_meta_errorlog_attribute](
	[sql_instance] [varchar](32) NOT NULL,
	[attribute_id] [smallint] IDENTITY(1,1) NOT NULL,
	[attribute_name] [varchar](255) NULL,
	[attribute_value] [varchar](255) NULL,
	[date_updated] [datetime] NOT NULL,
 CONSTRAINT [pk_sqlwatch_meta_errorlog_attributes] PRIMARY KEY CLUSTERED 
(
	[sql_instance] ASC,
	[attribute_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_meta_errorlog_keyword](
	[sql_instance] [varchar](32) NOT NULL,
	[keyword_id] [smallint] NOT NULL,
	[log_type_id] [int] NOT NULL,
	[keyword1] [nvarchar](255) NULL,
	[keyword2] [nvarchar](255) NULL,
	[date_updated] [datetime] NOT NULL,
 CONSTRAINT [pk_sqlwatch_meta_errorlog_keyword] PRIMARY KEY CLUSTERED 
(
	[sql_instance] ASC,
	[keyword_id] ASC,
	[log_type_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_meta_errorlog_text](
	[sql_instance] [varchar](32) NOT NULL,
	[errorlog_text_id] [int] IDENTITY(1,1) NOT NULL,
	[errorlog_text] [nvarchar](max) NULL,
	[total_occurence_count] [int] NULL,
	[first_occurence] [datetime] NULL,
	[last_occurence] [datetime] NULL,
	[date_updated] [datetime] NOT NULL,
 CONSTRAINT [pk_sqlwatch_meta_errorlog_text] PRIMARY KEY CLUSTERED 
(
	[sql_instance] ASC,
	[errorlog_text_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE VIEW [dbo].[vw_sqlwatch_logger_errorlog] WITH SCHEMABINDING
AS
SELECT le.[sql_instance]
      ,le.[log_date]
      ,log_type = case le.[log_type_id]
		when 1 then 'SQL Server'
		when 2 then 'SQL Agent'
		else 'Other'
		end
	  , ea.attribute_name
	  , ea.attribute_value
	  , et.errorlog_text
      , ek.keyword1
	  , ek.keyword2
  FROM [dbo].[sqlwatch_logger_errorlog] le
  inner join [dbo].[sqlwatch_meta_errorlog_attribute] ea
	on ea.[sql_instance] = le.sql_instance
	and ea.[attribute_id] = le.[attribute_id]
  inner join [dbo].[sqlwatch_meta_errorlog_text] et
	on et.sql_instance = le.sql_instance
	and et.[errorlog_text_id] = le.[errorlog_text_id]
  inner join dbo.sqlwatch_meta_errorlog_keyword ek
	on ek.sql_instance = le.sql_instance
	and ek.keyword_id = le.keyword_id;
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_config_check](
	[check_id] [bigint] IDENTITY(1,1) NOT NULL,
	[check_name] [nvarchar](255) NOT NULL,
	[check_description] [nvarchar](2048) NULL,
	[check_query] [nvarchar](max) NOT NULL,
	[check_frequency_minutes] [smallint] NULL,
	[check_threshold_warning] [varchar](100) NULL,
	[check_threshold_critical] [varchar](100) NOT NULL,
	[check_enabled] [bit] NOT NULL,
	[use_baseline] [bit] NOT NULL,
	[date_created] [datetime] NOT NULL,
	[date_updated] [datetime] NULL,
	[ignore_flapping] [bit] NOT NULL,
	[check_template_id] [smallint] NULL,
	[user_modified] [bit] NULL,
	[base_object_type] [varchar](50) NULL,
	[base_object_name] [nvarchar](128) NULL,
	[base_object_date_last_seen] [datetime2](0) NULL,
	[target_sql_instance] [varchar](32) NOT NULL,
 CONSTRAINT [pk_sqlwatch_config_check] PRIMARY KEY CLUSTERED 
(
	[check_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_config_check_action_template](
	[action_template_id] [smallint] IDENTITY(1,1) NOT NULL,
	[action_template_description] [varchar](1024) NULL,
	[action_template_fail_subject] [nvarchar](max) NOT NULL,
	[action_template_fail_body] [nvarchar](max) NOT NULL,
	[action_template_repeat_subject] [nvarchar](max) NOT NULL,
	[action_template_repeat_body] [nvarchar](max) NOT NULL,
	[action_template_recover_subject] [nvarchar](max) NOT NULL,
	[action_template_recover_body] [nvarchar](max) NOT NULL,
	[date_created] [datetime] NOT NULL,
	[date_updated] [datetime] NULL,
	[action_template_type] [varchar](50) NOT NULL,
 CONSTRAINT [pk_sqlwatch_config_action_template] PRIMARY KEY CLUSTERED 
(
	[action_template_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_config_report](
	[report_id] [smallint] IDENTITY(1,1) NOT NULL,
	[report_title] [varchar](255) NOT NULL,
	[report_description] [varchar](4000) NULL,
	[report_definition] [nvarchar](max) NOT NULL,
	[report_definition_type] [varchar](25) NOT NULL,
	[report_active] [bit] NOT NULL,
	[report_batch_id] [varchar](255) NULL,
	[report_style_id] [smallint] NULL,
	[date_created] [datetime] NULL,
	[date_updated] [datetime] NULL,
 CONSTRAINT [pk_sqlwatch_config_report] PRIMARY KEY CLUSTERED 
(
	[report_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_config_report_action](
	[report_id] [smallint] NOT NULL,
	[action_id] [smallint] NOT NULL,
 CONSTRAINT [pk_sqlwatch_config_report_action] PRIMARY KEY CLUSTERED 
(
	[report_id] ASC,
	[action_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE VIEW [dbo].[vw_sqlwatch_report_config_check_action] with schemabinding
	as
select 
	cc.check_id
	, check_name
	, cc.check_enabled
	, ca.action_id
	, ca.action_description
	, ca.action_enabled
	, ca.action_exec_type
	, ca.action_exec
	, at.action_template_id
	, at.action_template_description

	, ca.action_report_id
	, cr.report_title
	, cr.report_active
	, report_action_id=rca.action_id
	, report_action_description=rca.action_description
	, report_action_enabled=rca.action_enabled
	, report_action_exec_type=rca.action_exec_type
	, report_action_exec=rca.action_exec
from [dbo].[sqlwatch_config_check] cc
inner join [dbo].[sqlwatch_config_check_action] cca
	on cc.check_id = cca.check_id
inner join [dbo].[sqlwatch_config_action] ca
	on ca.action_id = cca.action_id
inner join [dbo].[sqlwatch_config_check_action_template] at
	on at.action_template_id = cca.action_template_id
left join [dbo].[sqlwatch_config_report] cr
	on cr.report_id = ca.action_report_id
left join [dbo].[sqlwatch_config_report_action] ra
	on ra.report_id = cr.report_id
left join [dbo].[sqlwatch_config_action] rca
	on rca.action_id = ra.action_id
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_meta_agent_job](
	[sql_instance] [varchar](32) NOT NULL,
	[job_name] [nvarchar](128) NOT NULL,
	[job_create_date] [datetime] NOT NULL,
	[sqlwatch_job_id] [smallint] IDENTITY(1,1) NOT NULL,
	[date_last_seen] [datetime] NULL,
	[is_record_deleted] [bit] NULL,
 CONSTRAINT [pk_sqlwatch_meta_agent_job] PRIMARY KEY CLUSTERED 
(
	[sql_instance] ASC,
	[sqlwatch_job_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY],
 CONSTRAINT [uq_sqlwatch_meta_agent_job_name] UNIQUE NONCLUSTERED 
(
	[sql_instance] ASC,
	[job_name] ASC,
	[job_create_date] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE VIEW [dbo].[vw_sqlwatch_report_dim_agent_job] with schemabinding
	AS 
	select [sql_instance], [job_name], [job_create_date], [sqlwatch_job_id], [date_last_seen] , [is_record_deleted]
	from dbo.sqlwatch_meta_agent_job
	where [is_record_deleted] = 0 or [is_record_deleted] is null;
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_meta_agent_job_step](
	[sql_instance] [varchar](32) NOT NULL,
	[sqlwatch_job_id] [smallint] NOT NULL,
	[step_name] [nvarchar](128) NOT NULL,
	[sqlwatch_job_step_id] [int] IDENTITY(1,1) NOT NULL,
	[date_last_seen] [datetime] NULL,
	[is_record_deleted] [bit] NULL,
 CONSTRAINT [pk_sqlwatch_meta_agent_job_step] PRIMARY KEY CLUSTERED 
(
	[sql_instance] ASC,
	[sqlwatch_job_id] ASC,
	[sqlwatch_job_step_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY],
 CONSTRAINT [uq_sqlwatch_meta_agent_job_step_name] UNIQUE NONCLUSTERED 
(
	[sql_instance] ASC,
	[sqlwatch_job_id] ASC,
	[step_name] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE VIEW [dbo].[vw_sqlwatch_report_dim_agent_job_step] with schemabinding
	AS 
	select [sql_instance], [sqlwatch_job_id], [step_name], [sqlwatch_job_step_id], [date_last_seen] , [is_record_deleted]
	from dbo.sqlwatch_meta_agent_job_step
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_meta_index](
	[sql_instance] [varchar](32) NOT NULL,
	[sqlwatch_database_id] [smallint] NOT NULL,
	[sqlwatch_table_id] [int] NOT NULL,
	[sqlwatch_index_id] [int] IDENTITY(1,1) NOT NULL,
	[index_name] [nvarchar](128) NULL,
	[index_id] [int] NOT NULL,
	[index_type_desc] [nvarchar](128) NULL,
	[date_created] [datetime] NOT NULL,
	[date_updated] [datetime] NULL,
	[date_last_seen] [datetime] NULL,
	[is_record_deleted] [bit] NULL,
 CONSTRAINT [pk_sqlwatch_meta_index] PRIMARY KEY CLUSTERED 
(
	[sql_instance] ASC,
	[sqlwatch_database_id] ASC,
	[sqlwatch_table_id] ASC,
	[sqlwatch_index_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE VIEW [dbo].[vw_sqlwatch_report_dim_index] with schemabinding
	AS 
select [sql_instance], [sqlwatch_database_id], [sqlwatch_table_id], [sqlwatch_index_id], [index_name], [index_id], [index_type_desc]
	, [date_created], [date_updated], [date_last_seen], [is_record_deleted]
from dbo.sqlwatch_meta_index
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_meta_index_missing](
	[sql_instance] [varchar](32) NOT NULL,
	[sqlwatch_database_id] [smallint] NOT NULL,
	[sqlwatch_table_id] [int] NOT NULL,
	[sqlwatch_missing_index_id] [int] IDENTITY(1,1) NOT NULL,
	[equality_columns] [nvarchar](max) NULL,
	[inequality_columns] [nvarchar](max) NULL,
	[included_columns] [nvarchar](max) NULL,
	[statement] [nvarchar](max) NULL,
	[index_handle] [int] NULL,
	[date_created] [datetime] NOT NULL,
	[date_last_seen] [datetime] NULL,
	[is_record_deleted] [bit] NULL,
 CONSTRAINT [pk_sqlwatch_meta_index_missing] PRIMARY KEY CLUSTERED 
(
	[sql_instance] ASC,
	[sqlwatch_database_id] ASC,
	[sqlwatch_table_id] ASC,
	[sqlwatch_missing_index_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE VIEW [dbo].[vw_sqlwatch_report_dim_index_missing] with schemabinding
	AS 
	select [sql_instance], [sqlwatch_database_id], [sqlwatch_table_id], [sqlwatch_missing_index_id], [equality_columns], [inequality_columns], [included_columns], [statement]
	, [index_handle], [date_created], [date_last_seen] , [is_record_deleted]
	from dbo.sqlwatch_meta_index_missing
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_meta_server](
	[physical_name] [nvarchar](128) NULL,
	[servername] [varchar](32) NOT NULL,
	[service_name] [nvarchar](128) NULL,
	[local_net_address] [varchar](50) NULL,
	[local_tcp_port] [varchar](50) NULL,
	[utc_offset_minutes] [int] NOT NULL,
	[sql_version] [nvarchar](2048) NULL,
	[date_updated] [datetime] NOT NULL,
	[sql_instance]  AS ([servername]),
 CONSTRAINT [pk_sqlwatch_meta_server] PRIMARY KEY CLUSTERED 
(
	[servername] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE VIEW [dbo].[vw_sqlwatch_report_dim_server] with schemabinding
as
SELECT [physical_name]
      ,sql_instance = [servername]
	  ,[servername] --PBI backward compatibility with old reports.
      ,[service_name]
      ,[local_net_address]
      ,[local_tcp_port]
      ,d.[utc_offset_minutes]
	  ,c.environment
	  ,d.sql_version 
      ,c.[sql_instance_user_alias]
  FROM [dbo].[sqlwatch_meta_server] d
  inner join dbo.sqlwatch_config_sql_instance c
	on d.servername = c.sql_instance
  /* ignore any remote instances that are not being collected */
  where case when servername <> @@SERVERNAME then 1 else 0 end = c.repo_collector_is_active
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE VIEW [dbo].[vw_sqlwatch_report_dim_time] with schemabinding
as

select 
	  [sql_instance]
	, [snapshot_type_id]
	, [snapshot_time]
	, [report_time] 
	, [date] = convert(date,report_time)
	, [year] = datepart(year,report_time)
	, [month] = datepart(month, report_time)
	, [day] = datepart(day, report_time)
	, [hour] = datepart(hour, report_time)
	, [minute] = datepart(minute, report_time)
	, [time] = convert(time,report_time)
	, [month_name] = datename(month, report_time)
	, [week_number] = datename (wk, report_time)
	, [week_day] = datename (weekday, report_time)
	, [day_of_year] = datename (dayofyear, report_time)
	, [year_month] = convert(char(4),datepart(year,report_time)) + '-' + right('00' + convert(char(2),datepart(month, report_time)),2)
	, [day_of_week] = datepart(dw, report_time)
	, [year_week] = convert(char(4),datepart(year,report_time)) + '-' + right('WK' + convert(char(2),datename (wk, report_time)),4)

	/*	calculate time intervals for dynamic grouping in PBI
		based on the time interval parameter we can aggregate over 5, 15 or 16 minutes to reduce data pulled into PBI
		or we can show at 1 minute intervals - highest granularity */
	, interval_minutes_5 = convert(smalldatetime,dateadd(minute,(datediff(minute,0, report_time)/ 5) * 5,0))
	, interval_minutes_15 = convert(smalldatetime,dateadd(minute,(datediff(minute,0, report_time)/ 15) * 15,0))
	, interval_minutes_60 = convert(smalldatetime,dateadd(minute,(datediff(minute,0, report_time)/ 60) * 60,0))

	/*	calcuate dates for baselines so we can join "snapshot_time" on one of the below columns to get historical
		values for the same "current" period */
	, baseline_1_report_time = dateadd(DAY,-1,report_time)
	, baseline_2_report_time = dateadd(WEEK,-1,report_time)
	, baseline_3_report_time = dateadd(MONTH,-1,report_time)

	, baseline_1_snapshot_time = dateadd(DAY,-1,snapshot_time)
	, baseline_2_snapshot_time = dateadd(WEEK,-1,snapshot_time)
	, baseline_3_snapshot_time = dateadd(MONTH,-1,snapshot_time)
	--, report_time_utc = dateadd(minute,([snapshot_time_utc_offset]*-1),report_time)
from dbo.sqlwatch_logger_snapshot_header
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_logger_agent_job_history](
	[sql_instance] [varchar](32) NOT NULL,
	[sqlwatch_job_id] [smallint] NOT NULL,
	[sqlwatch_job_step_id] [int] NOT NULL,
	[sysjobhistory_instance_id] [int] NOT NULL,
	[sysjobhistory_step_id] [int] NOT NULL,
	[run_duration_s] [real] NOT NULL,
	[run_date] [datetime] NOT NULL,
	[run_status] [tinyint] NOT NULL,
	[snapshot_time] [datetime2](0) NOT NULL,
	[snapshot_type_id] [tinyint] NOT NULL,
	[run_date_utc] [datetime] NOT NULL,
 CONSTRAINT [pk_sqlwatch_logger_agent_job_history] PRIMARY KEY CLUSTERED 
(
	[sql_instance] ASC,
	[snapshot_time] ASC,
	[sqlwatch_job_id] ASC,
	[sqlwatch_job_step_id] ASC,
	[sysjobhistory_instance_id] ASC,
	[snapshot_type_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, DATA_COMPRESSION = PAGE) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE VIEW [dbo].[vw_sqlwatch_report_fact_agent_job_history] with schemabinding
as
select 
	  jh.sqlwatch_job_id
	, jh.sqlwatch_job_step_id
	, jh.sql_instance

	, j.job_name
	, js.step_name

	, jh.[sysjobhistory_step_id]
	, [run_duration_s]
	, [run_date]
	, [run_status]
	, [run_status_desc] = case [run_status]
			when 0 then 'Failed'
			when 1 then 'Succeeded'
			when 2 then 'Retry'
			when 3 then 'Canceled'
			when 4 then 'In Progress'
		else 'Unknown Status' end
	,report_time
	,[end_date]=dateadd(s,[run_duration_s],[run_date])
	,[show_agent_history] = convert(bit,1)
 --for backward compatibility with existing pbi, this column will become report_time as we could be aggregating many snapshots in a report_period
, jh.snapshot_time
, sh.snapshot_type_id
, jh.run_date_utc
from [dbo].[sqlwatch_logger_agent_job_history] jh

inner join dbo.sqlwatch_logger_snapshot_header sh
	on sh.sql_instance = jh.sql_instance
	and sh.snapshot_time = jh.[snapshot_time]
	and sh.snapshot_type_id = jh.snapshot_type_id

inner join [dbo].[sqlwatch_meta_agent_job_step] js
	on jh.sql_instance = js.sql_instance
	and jh.sqlwatch_job_id = js.sqlwatch_job_id
	and jh.sqlwatch_job_step_id = js.sqlwatch_job_step_id

inner join [dbo].[sqlwatch_meta_agent_job] j
	on j.sql_instance = jh.sql_instance
	and j.sqlwatch_job_id = jh.sqlwatch_job_id
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE VIEW [dbo].[vw_sqlwatch_report_fact_check] 
with schemabinding
	AS 

select 
	  [d].[sql_instance]
	, [d].[snapshot_time]
	, [d].[check_id]
	, c.check_name
	, [d].[check_value]
	, [d].[check_status]
	, [d].[check_exec_time_ms]
	, h.report_time
	, [d].status_change
	, d.snapshot_type_id
	, c.check_description
	, c.target_sql_instance
from dbo.sqlwatch_logger_check d
  	inner join dbo.sqlwatch_logger_snapshot_header h
		on  h.snapshot_time = d.[snapshot_time]
		and h.snapshot_type_id = d.snapshot_type_id
		and h.sql_instance = d.sql_instance

	inner join [dbo].[sqlwatch_meta_check] c
		on c.sql_instance = d.sql_instance
		and c.check_id = d.check_id
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE VIEW [dbo].[vw_sqlwatch_report_fact_check_status_change] with schemabinding
	as
	select 
	l1.sql_instance,
	l1.check_id,
	l1.check_status,
	[status_start_time] = l1.snapshot_time,
	[status_end_time] = isnull(t.snapshot_time,getutcdate())
	, h.report_time
	, check_count = t.check_count
	, l1.snapshot_type_id
from [dbo].[sqlwatch_logger_check] l1
inner join dbo.sqlwatch_logger_snapshot_header h
	on h.snapshot_time = l1.snapshot_time
	and h.sql_instance = l1.sql_instance
	and h.snapshot_type_id = l1.snapshot_type_id
outer apply (
		select 
			snapshot_time=max(snapshot_time),
			check_count=count(*)
		from [dbo].[sqlwatch_logger_check]
		where check_id = l1.check_id
		and sql_instance = l1.sql_instance
		and snapshot_time > l1.snapshot_time
		and status_change = 0
) t
where l1.status_change = 1
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE VIEW [dbo].[vw_sqlwatch_report_fact_disk_utilisation_database] with schemabinding
	AS 
SELECT d.[sqlwatch_database_id]
	  ,db.[database_name]
      ,d.[database_size_bytes]
      ,d.[unallocated_space_bytes]
      ,d.[reserved_bytes]
      ,d.[data_bytes]
      ,d.[index_size_bytes]
      ,d.[unused_bytes]
      ,d.[log_size_total_bytes]
      ,d.[log_size_used_bytes]
      ,h.report_time
      ,d.[sql_instance]

	  ,d.[unallocated_extent_page_count] 
	  ,d.[allocated_extent_page_count] 
	  ,d.[version_store_reserved_page_count] 
	  ,d.[user_object_reserved_page_count] 
	  ,d.[internal_object_reserved_page_count] 
	  ,d.[mixed_extent_page_count] 

 --for backward compatibility with existing pbi, this column will become report_time as we could be aggregating many snapshots in a report_period
, d.snapshot_time
, d.snapshot_type_id
  FROM [dbo].[sqlwatch_logger_disk_utilisation_database] d

  	inner join dbo.sqlwatch_logger_snapshot_header h
		on  h.snapshot_time = d.[snapshot_time]
		and h.snapshot_type_id = d.snapshot_type_id
		and h.sql_instance = d.sql_instance

	inner join [dbo].[sqlwatch_meta_database] db
	on db.sql_instance = d.sql_instance
	and db.sqlwatch_database_id = d.sqlwatch_database_id
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE VIEW [dbo].[vw_sqlwatch_report_fact_disk_utilisation_table] with schemabinding
as

select mdb.[database_name]
	  ,mdb.database_create_date
	  ,mt.table_name
      ,[row_count]
      ,[total_pages]
      ,[used_pages]
      ,[data_compression]
      ,h.[snapshot_type_id]
      ,h.[snapshot_time]
	  ,h.report_time
      ,ut.[sql_instance]
	  ,[row_count_delta]
	  ,[total_pages_delta]
	  ,[used_pages_delta]
	  ,ut.sqlwatch_database_id
	  ,ut.sqlwatch_table_id
  from [dbo].[sqlwatch_logger_disk_utilisation_table] ut
  
  inner join [dbo].[sqlwatch_meta_table] mt
	on mt.[sqlwatch_table_id] = ut.[sqlwatch_table_id]
	and mt.[sqlwatch_database_id] = ut.[sqlwatch_database_id]
	and mt.sql_instance = ut.sql_instance

  inner join [dbo].[sqlwatch_meta_database] mdb
	on mdb.sqlwatch_database_id = mt.sqlwatch_database_id
	and mdb.sql_instance = mt.sql_instance

  	inner join dbo.sqlwatch_logger_snapshot_header h
		on  h.snapshot_time = ut.[snapshot_time]
		and h.snapshot_type_id = ut.snapshot_type_id
		and h.sql_instance = ut.sql_instance
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE VIEW [dbo].[vw_sqlwatch_report_fact_disk_utilisation_volume] with schemabinding
as

SELECT d.[sqlwatch_volume_id]
	  ,v.volume_name
      ,[volume_free_space_bytes]
      ,[volume_total_space_bytes]
      ,h.report_time
      ,d.[sql_instance]
 --for backward compatibility with existing pbi, this column will become report_time as we could be aggregating many snapshots in a report_period
, d.snapshot_time
, d.snapshot_type_id
  FROM [dbo].[sqlwatch_logger_disk_utilisation_volume] d

  	inner join dbo.sqlwatch_logger_snapshot_header h
		on  h.snapshot_time = d.[snapshot_time]
		and h.snapshot_type_id = d.snapshot_type_id
		and h.sql_instance = d.sql_instance

	inner join [dbo].[sqlwatch_meta_os_volume] v
	on v.sql_instance = d.sql_instance
	and v.sqlwatch_volume_id = d.sqlwatch_volume_id
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_logger_index_histogram](
	[sqlwatch_database_id] [smallint] NOT NULL,
	[sqlwatch_table_id] [int] NOT NULL,
	[sqlwatch_index_id] [int] NOT NULL,
	[sqlwatch_stat_range_id] [bigint] IDENTITY(1,1) NOT NULL,
	[RANGE_HI_KEY] [nvarchar](max) NULL,
	[RANGE_ROWS] [real] NULL,
	[EQ_ROWS] [real] NULL,
	[DISTINCT_RANGE_ROWS] [real] NULL,
	[AVG_RANGE_ROWS] [real] NULL,
	[snapshot_time] [datetime2](0) NOT NULL,
	[snapshot_type_id] [tinyint] NOT NULL,
	[collection_time] [datetime] NULL,
	[sql_instance] [varchar](32) NOT NULL,
 CONSTRAINT [pk_logger_index_histogram] PRIMARY KEY CLUSTERED 
(
	[snapshot_time] ASC,
	[sql_instance] ASC,
	[sqlwatch_database_id] ASC,
	[sqlwatch_table_id] ASC,
	[sqlwatch_index_id] ASC,
	[sqlwatch_stat_range_id] ASC,
	[snapshot_type_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, DATA_COMPRESSION = PAGE) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE VIEW [dbo].[vw_sqlwatch_report_fact_index_histogram] with schemabinding
as

SELECT d.[sqlwatch_database_id]
      ,d.[sqlwatch_table_id]
      ,d.[sqlwatch_index_id]
      ,d.[sqlwatch_stat_range_id]
      ,d.[RANGE_HI_KEY]
      ,d.[RANGE_ROWS]
      ,d.[EQ_ROWS]
      ,d.[DISTINCT_RANGE_ROWS]
      ,d.[AVG_RANGE_ROWS]
      ,report_time
      ,d.[collection_time]
      ,d.[sql_instance]
	  ,is_latest = case when d.snapshot_time = t.snapshot_time and d.sql_instance = t.sql_instance then 1 else 0 end
 --for backward compatibility with existing pbi, this column will become report_time as we could be aggregating many snapshots in a report_period
, d.snapshot_time
, d.snapshot_type_id
  FROM [dbo].[sqlwatch_logger_index_histogram] d
  	inner join dbo.sqlwatch_logger_snapshot_header h
		on  h.snapshot_time = d.[snapshot_time]
		and h.snapshot_type_id = d.snapshot_type_id
		and h.sql_instance = d.sql_instance

	outer apply (
		select sql_instance 
			, snapshot_time=max(snapshot_time)
			, snapshot_type_id 
		from dbo.sqlwatch_logger_snapshot_header h
		where sql_instance = d.sql_instance
		and snapshot_type_id = d.snapshot_type_id
		group by sql_instance, snapshot_type_id
		) t
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_logger_index_missing_stats](
	[sqlwatch_database_id] [smallint] NOT NULL,
	[sqlwatch_table_id] [int] NOT NULL,
	[sqlwatch_missing_index_id] [int] NOT NULL,
	[sqlwatch_missing_index_stats_id] [int] IDENTITY(1,1) NOT NULL,
	[snapshot_time] [datetime2](0) NOT NULL,
	[last_user_seek] [datetime] NULL,
	[unique_compiles] [bigint] NULL,
	[user_seeks] [bigint] NULL,
	[user_scans] [bigint] NULL,
	[avg_total_user_cost] [float] NULL,
	[avg_user_impact] [float] NULL,
	[snapshot_type_id] [tinyint] NOT NULL,
	[sql_instance] [varchar](32) NOT NULL,
 CONSTRAINT [pk_logger_missing_indexes] PRIMARY KEY CLUSTERED 
(
	[sql_instance] ASC,
	[snapshot_time] ASC,
	[sqlwatch_database_id] ASC,
	[sqlwatch_table_id] ASC,
	[sqlwatch_missing_index_id] ASC,
	[sqlwatch_missing_index_stats_id] ASC,
	[snapshot_type_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, DATA_COMPRESSION = PAGE) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE VIEW [dbo].[vw_sqlwatch_report_fact_index_missing_stats] with schemabinding
as
SELECT d.[sqlwatch_database_id]
      ,d.[sqlwatch_table_id]
      ,d.[sqlwatch_missing_index_id]
      ,d.[sqlwatch_missing_index_stats_id]
      ,report_time
      ,[last_user_seek]
      ,[unique_compiles]
      ,[user_seeks]
      ,[user_scans]
      ,[avg_total_user_cost]
      ,[avg_user_impact]
      ,d.[sql_instance]
 --for backward compatibility with existing pbi, this column will become report_time as we could be aggregating many snapshots in a report_period
, d.snapshot_time
, d.snapshot_type_id
  FROM [dbo].[sqlwatch_logger_index_missing_stats] d
  	inner join dbo.sqlwatch_logger_snapshot_header h
		on  h.snapshot_time = d.[snapshot_time]
		and h.snapshot_type_id = d.snapshot_type_id
		and h.sql_instance = d.sql_instance
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_logger_index_usage_stats](
	[sqlwatch_database_id] [smallint] NOT NULL,
	[sqlwatch_table_id] [int] NOT NULL,
	[sqlwatch_index_id] [int] NOT NULL,
	[used_pages_count] [real] NULL,
	[user_seeks] [real] NOT NULL,
	[user_scans] [real] NOT NULL,
	[user_lookups] [real] NOT NULL,
	[user_updates] [real] NOT NULL,
	[last_user_seek] [datetime] NULL,
	[last_user_scan] [datetime] NULL,
	[last_user_lookup] [datetime] NULL,
	[last_user_update] [datetime] NULL,
	[stats_date] [datetime] NULL,
	[snapshot_time] [datetime2](0) NOT NULL,
	[snapshot_type_id] [tinyint] NOT NULL,
	[index_disabled] [bit] NULL,
	[sql_instance] [varchar](32) NOT NULL,
	[partition_id] [bigint] NOT NULL,
	[used_pages_count_delta] [real] NULL,
	[user_seeks_delta] [real] NULL,
	[user_scans_delta] [real] NULL,
	[user_updates_delta] [real] NULL,
	[delta_seconds] [int] NULL,
	[user_lookups_delta] [real] NULL,
	[partition_count] [real] NULL,
	[partition_count_delta] [real] NULL,
 CONSTRAINT [pk_index_usage_stats] PRIMARY KEY CLUSTERED 
(
	[snapshot_time] ASC,
	[sql_instance] ASC,
	[sqlwatch_database_id] ASC,
	[sqlwatch_table_id] ASC,
	[sqlwatch_index_id] ASC,
	[partition_id] ASC,
	[snapshot_type_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, DATA_COMPRESSION = PAGE) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE VIEW [dbo].[vw_sqlwatch_report_fact_index_usage_stats] with schemabinding
as
select [sqlwatch_database_id], [sqlwatch_table_id], [sqlwatch_index_id], [used_pages_count], [user_seeks], [user_scans], [user_lookups], [user_updates], [last_user_seek]
, [last_user_scan], [last_user_lookup], [last_user_update], [stats_date], report_time, [index_disabled], d.[sql_instance]
, [partition_id], [used_pages_count_delta], [user_seeks_delta], [user_scans_delta], [user_updates_delta], [delta_seconds]
, [user_lookups_delta]

, [update_ratio] = case when isnull([user_seeks]+[user_lookups]+[user_scans]+[user_updates],0) > 0 
	then [user_updates]/([user_seeks]+[user_lookups]+[user_scans]+[user_updates]) else 0 end 

, [scan_to_seek_ratio_delta] = case when isnull([user_seeks]+[user_scans]+[user_lookups],0) > 0 
	then [user_scans]/([user_seeks]+[user_scans]+[user_lookups]) else 0 end

, [update_ratio_delta] = case when isnull([user_seeks_delta]+[user_lookups_delta]+[user_scans_delta]+[user_updates_delta],0) > 0 
	then [user_updates_delta]/([user_seeks_delta]+[user_lookups_delta]+[user_scans_delta]+[user_updates_delta]) else 0 end 

, [scan_to_seek_ratio] = case when isnull([user_seeks_delta]+[user_scans_delta]+[user_lookups_delta],0) > 0 
	then [user_scans_delta]/([user_seeks_delta]+[user_scans_delta]+[user_lookups_delta]) else 0 end 

, [index_status] = case when [index_disabled] = 1 then 'DISABLED' else 'Enabled' end
 --for backward compatibility with existing pbi, this column will become report_time as we could be aggregating many snapshots in a report_period
, d.snapshot_time
, show_usage_stats = convert(bit,1)
, d.snapshot_type_id
from [dbo].[sqlwatch_logger_index_usage_stats] d
  	inner join dbo.sqlwatch_logger_snapshot_header h
		on  h.snapshot_time = d.[snapshot_time]
		and h.snapshot_type_id = d.snapshot_type_id
		and h.sql_instance = d.sql_instance
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_logger_perf_os_memory_clerks](
	[snapshot_time] [datetime2](0) NOT NULL,
	[total_kb] [bigint] NULL,
	[allocated_kb] [bigint] NULL,
	[sqlwatch_mem_clerk_id] [smallint] NOT NULL,
	[snapshot_type_id] [tinyint] NOT NULL,
	[sql_instance] [varchar](32) NOT NULL,
 CONSTRAINT [pk_sql_perf_mon_os_memory_clerks] PRIMARY KEY CLUSTERED 
(
	[snapshot_time] ASC,
	[snapshot_type_id] ASC,
	[sql_instance] ASC,
	[sqlwatch_mem_clerk_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, DATA_COMPRESSION = PAGE) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_meta_memory_clerk](
	[sql_instance] [varchar](32) NOT NULL,
	[sqlwatch_mem_clerk_id] [smallint] IDENTITY(1,1) NOT NULL,
	[clerk_name] [nvarchar](255) NOT NULL,
	[date_updated] [datetime] NOT NULL,
 CONSTRAINT [pk_sqlwatch_meta_memory_clerk] PRIMARY KEY CLUSTERED 
(
	[sql_instance] ASC,
	[sqlwatch_mem_clerk_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY],
 CONSTRAINT [uq_sqlwatch_meta_memory_clerk] UNIQUE NONCLUSTERED 
(
	[sql_instance] ASC,
	[clerk_name] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE VIEW [dbo].[vw_sqlwatch_report_fact_perf_os_memory_clerks] with schemabinding
as
SELECT [report_time]
      ,omc.[total_kb]
      ,omc.[allocated_kb]
      ,omc.[sql_instance]
	  ,mdc.clerk_name
	  , omc.snapshot_type_id
 --for backward compatibility with existing pbi, this column will become report_time as we could be aggregating many snapshots in a report_period
, omc.snapshot_time
, omc.sqlwatch_mem_clerk_id
  FROM [dbo].[sqlwatch_logger_perf_os_memory_clerks] omc
	
	inner join [dbo].[sqlwatch_meta_memory_clerk] mdc
		on mdc.sql_instance = omc.sql_instance
		and mdc.sqlwatch_mem_clerk_id = omc.sqlwatch_mem_clerk_id

    inner join dbo.sqlwatch_logger_snapshot_header sh
		on sh.sql_instance = omc.sql_instance
		and sh.snapshot_time = omc.[snapshot_time]
		and sh.snapshot_type_id = omc.snapshot_type_id
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_logger_perf_os_performance_counters](
	[performance_counter_id] [smallint] NOT NULL,
	[instance_name] [nvarchar](128) NOT NULL,
	[cntr_value] [bigint] NOT NULL,
	[base_cntr_value] [bigint] NULL,
	[snapshot_time] [datetime2](0) NOT NULL,
	[snapshot_type_id] [tinyint] NOT NULL,
	[sql_instance] [varchar](32) NOT NULL,
	[cntr_value_calculated] [real] NULL,
 CONSTRAINT [pk_sql_perf_mon_perf_counters] PRIMARY KEY CLUSTERED 
(
	[snapshot_time] ASC,
	[snapshot_type_id] ASC,
	[sql_instance] ASC,
	[performance_counter_id] ASC,
	[instance_name] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, DATA_COMPRESSION = PAGE) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_meta_performance_counter](
	[sql_instance] [varchar](32) NOT NULL,
	[object_name] [nvarchar](128) NOT NULL,
	[counter_name] [nvarchar](128) NOT NULL,
	[cntr_type] [int] NOT NULL,
	[performance_counter_id] [smallint] IDENTITY(1,1) NOT NULL,
	[date_updated] [datetime] NOT NULL,
	[is_sql_counter] [bit] NULL,
 CONSTRAINT [pk_sqlwatch_meta_performance_counter] PRIMARY KEY CLUSTERED 
(
	[sql_instance] ASC,
	[performance_counter_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY],
 CONSTRAINT [uq_sqlwatch_meta_performance_counter_object] UNIQUE NONCLUSTERED 
(
	[sql_instance] ASC,
	[object_name] ASC,
	[counter_name] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_trend_perf_os_performance_counters](
	[performance_counter_id] [smallint] NOT NULL,
	[instance_name] [nvarchar](128) NOT NULL,
	[sql_instance] [varchar](32) NOT NULL,
	[cntr_value_calculated_avg] [real] NULL,
	[cntr_value_calculated_min] [real] NULL,
	[cntr_value_calculated_max] [real] NULL,
	[cntr_value_calculated_sum] [real] NULL,
	[interval_minutes] [tinyint] NOT NULL,
	[snapshot_time] [datetime2](0) NOT NULL,
	[snapshot_time_offset] [datetimeoffset](0) NULL,
	[valid_until] [datetime2](0) NULL,
 CONSTRAINT [pk_sqlwatch_trend_perf_os_performance_counters] PRIMARY KEY CLUSTERED 
(
	[snapshot_time] ASC,
	[instance_name] ASC,
	[sql_instance] ASC,
	[interval_minutes] ASC,
	[performance_counter_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, DATA_COMPRESSION = PAGE) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE VIEW [dbo].[vw_sqlwatch_report_fact_perf_os_performance_counters] with schemabinding
as

select 
	m.[object_name]
	, m.[counter_name]
	, [instance_name]
	, [cntr_value_raw]=[cntr_value]
	, report_time
	, d.[sql_instance]
	, [cntr_value_calculated]
	--, pcp.desired_value_desc, pcp.desired_value, pcp.description
	, [aggregation_interval_minutes] = 0
	, d.snapshot_type_id
	, d.snapshot_time
	, d.performance_counter_id
	, is_trend = 0
from [dbo].[sqlwatch_logger_perf_os_performance_counters] d
  	
	inner join dbo.sqlwatch_logger_snapshot_header h
		on  h.snapshot_time = d.[snapshot_time]
		and h.snapshot_type_id = d.snapshot_type_id
		and h.sql_instance = d.sql_instance

	inner join [dbo].[sqlwatch_meta_performance_counter] m
		on m.sql_instance = d.sql_instance
		and m.performance_counter_id = d.performance_counter_id

	where m.cntr_type <> 1073939712

	/* aggregated data. we are going to have to specify aggregataion level for every select */
	union all

	/* TO DO this table needs actual report_time of utc offset otherwise we wont be able to use it */
	select m.[object_name], m.[counter_name], [instance_name], [cntr_value]=null
	, report_time = convert(datetime2(0),snapshot_time_offset)
	, d.[sql_instance]
	, [cntr_value_calculated_avg]
	, [aggregation_interval_minutes] = [interval_minutes] 
	, snapshot_type_id = 1
	, snapshot_time = convert(datetime2(0),snapshot_time_offset)
	, d.performance_counter_id
	, is_trend = 1
	from [dbo].[sqlwatch_trend_perf_os_performance_counters] d
	inner join [dbo].[sqlwatch_meta_performance_counter] m
		on m.sql_instance = d.sql_instance
		and m.performance_counter_id = d.performance_counter_id
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE VIEW [dbo].[vw_sqlwatch_report_fact_perf_os_performance_counters_rate] with schemabinding
	as
/*
-------------------------------------------------------------------------------------------------------------------
 View:
	vw_sqlwatch_report_fact_perf_os_performance_counters_rate

 Description:
	Rate calculations are a combination of various counters to give a specific rate.
	We are going to pivot counters of interest for easy divide then unpivot back in the original format.
	There may be more efficient ways but this is quite easy and transparent, also quite quick < 9ms exec time.
	
 Author:
	Marcin Gminski

 Change Log:
	1.0		2019-12-25		- Marcin Gminski, Initial version
-------------------------------------------------------------------------------------------------------------------
*/

with cte_pivot as (
	select sql_instance, snapshot_time, report_time
		, [Full Scan Rate] = case when [Index Searches/sec] > 0 then [Full Scans/sec] / [Index Searches/sec] else 0 end
		, [SQL Compilations Rate] = case when [Batch Requests/Sec] > 0 then [SQL Compilations/sec] / [Batch Requests/Sec] else 0 end
		, [SQL Re-Compilation Rate] = case when [SQL Compilations/sec] > 0 then [SQL Re-Compilations/sec] / [SQL Compilations/sec] else 0 end
		, [Page Split Rate] = case when [Batch Requests/Sec] > 0 then [Page Splits/sec] / [Batch Requests/Sec] else 0 end
		, [Page Lookups Rate] = case when [Batch Requests/Sec] > 0 then [Page lookups/sec] / [Batch Requests/Sec] else 0 end
	from  
	(	
		select sql_instance, snapshot_time, counter_name, cntr_value_calculated, report_time
		from [dbo].[vw_sqlwatch_report_fact_perf_os_performance_counters]
	) as src  
	pivot  
	(  
	avg(cntr_value_calculated)  
	for counter_name IN (
		  [Index Searches/sec]
		, [Full Scans/sec]
		, [SQL Compilations/sec]
		, [Batch Requests/Sec]
		, [SQL Re-Compilations/sec]
		, [Page Splits/sec]
		, [Page lookups/sec]
		)  
	) as pvt
)

select [sql_instance]
	, [snapshot_time]
	, [cntr_value_calculated]
	, [counter_name]
	, [report_time]
from 
   (select [sql_instance]
	, [snapshot_time]
	, [Full Scan Rate]
	, [SQL Compilations Rate]
	, [SQL Re-Compilation Rate]
	, [Page Split Rate]
	, [Page Lookups Rate]
	, [report_time]
   from cte_pivot) p  
unpivot  
   (cntr_value_calculated for counter_name IN   
      (	  
		  [Full Scan Rate]
		, [SQL Compilations Rate]
		, [SQL Re-Compilation Rate]
		, [Page Split Rate]
		, [Page Lookups Rate]
		)  
) as unpvt;
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_logger_perf_os_process_memory](
	[snapshot_time] [datetime2](0) NOT NULL,
	[physical_memory_in_use_kb] [bigint] NOT NULL,
	[large_page_allocations_kb] [bigint] NOT NULL,
	[locked_page_allocations_kb] [bigint] NOT NULL,
	[total_virtual_address_space_kb] [bigint] NOT NULL,
	[virtual_address_space_reserved_kb] [bigint] NOT NULL,
	[virtual_address_space_committed_kb] [bigint] NOT NULL,
	[virtual_address_space_available_kb] [bigint] NOT NULL,
	[page_fault_count] [bigint] NOT NULL,
	[memory_utilization_percentage] [int] NOT NULL,
	[available_commit_limit_kb] [bigint] NOT NULL,
	[process_physical_memory_low] [bit] NOT NULL,
	[process_virtual_memory_low] [bit] NOT NULL,
	[snapshot_type_id] [tinyint] NOT NULL,
	[sql_instance] [varchar](32) NOT NULL,
 CONSTRAINT [pk_sql_perf_mon_os_process_memory] PRIMARY KEY CLUSTERED 
(
	[snapshot_time] ASC,
	[snapshot_type_id] ASC,
	[sql_instance] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, DATA_COMPRESSION = PAGE) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE VIEW [dbo].[vw_sqlwatch_report_fact_perf_os_process_memory] with schemabinding
as
SELECT report_time
      ,[physical_memory_in_use_kb]
      ,[large_page_allocations_kb]
      ,[locked_page_allocations_kb]
      ,[total_virtual_address_space_kb]
      ,[virtual_address_space_reserved_kb]
      ,[virtual_address_space_committed_kb]
      ,[virtual_address_space_available_kb]
      ,[page_fault_count]
      ,[memory_utilization_percentage]
      ,[available_commit_limit_kb]
      ,[process_physical_memory_low]
      ,[process_virtual_memory_low]
      ,d.[sql_instance]
	  ,d.snapshot_type_id
 --for backward compatibility with existing pbi, this column will become report_time as we could be aggregating many snapshots in a report_period
, d.snapshot_time
  FROM [dbo].[sqlwatch_logger_perf_os_process_memory] d
  	inner join dbo.sqlwatch_logger_snapshot_header h
		on  h.snapshot_time = d.[snapshot_time]
		and h.snapshot_type_id = d.snapshot_type_id
		and h.sql_instance = d.sql_instance
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_logger_perf_os_schedulers](
	[snapshot_time] [datetime2](0) NOT NULL,
	[snapshot_type_id] [tinyint] NOT NULL,
	[scheduler_count] [smallint] NULL,
	[idle_scheduler_count] [smallint] NULL,
	[current_tasks_count] [int] NULL,
	[runnable_tasks_count] [int] NULL,
	[preemptive_switches_count] [bigint] NULL,
	[context_switches_count] [bigint] NULL,
	[idle_switches_count] [bigint] NULL,
	[current_workers_count] [int] NULL,
	[active_workers_count] [int] NULL,
	[work_queue_count] [int] NULL,
	[pending_disk_io_count] [int] NULL,
	[load_factor] [int] NULL,
	[yield_count] [bigint] NULL,
	[failed_to_create_worker] [int] NULL,
	[total_cpu_usage_ms] [bigint] NULL,
	[total_scheduler_delay_ms] [bigint] NULL,
	[sql_instance] [varchar](32) NOT NULL,
 CONSTRAINT [pk_logger_perf_os_schedulers] PRIMARY KEY CLUSTERED 
(
	[snapshot_time] ASC,
	[snapshot_type_id] ASC,
	[sql_instance] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, DATA_COMPRESSION = PAGE) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE VIEW [dbo].[vw_sqlwatch_report_fact_perf_os_schedulers] with schemabinding
as
SELECT report_time
      ,[scheduler_count]
      ,[idle_scheduler_count]
      ,[current_tasks_count]
      ,[runnable_tasks_count]
      ,[preemptive_switches_count]
      ,[context_switches_count]
      ,[idle_switches_count]
      ,[current_workers_count]
      ,[active_workers_count]
      ,[work_queue_count]
      ,[pending_disk_io_count]
      ,[load_factor]
      ,[yield_count]
      ,[failed_to_create_worker]
      ,[total_cpu_usage_ms]
      ,[total_scheduler_delay_ms]
      ,d.[sql_instance]
	  ,d.snapshot_type_id
 --for backward compatibility with existing pbi, this column will become report_time as we could be aggregating many snapshots in a report_period
, d.snapshot_time
  FROM [dbo].[sqlwatch_logger_perf_os_schedulers] d
  	inner join dbo.sqlwatch_logger_snapshot_header h
		on  h.snapshot_time = d.[snapshot_time]
		and h.snapshot_type_id = d.snapshot_type_id
		and h.sql_instance = d.sql_instance
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE FUNCTION [dbo].[ufn_sqlwatch_convert_datetimeoffset_to_local] 
(
	@datetimeoffset datetimeoffset
)

returns datetime2(0) 
with schemabinding
as
begin
	return convert(datetime, switchoffset(@datetimeoffset, datename(TzOffset, @datetimeoffset)))
end
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE FUNCTION [dbo].[ufn_sqlwatch_get_database_create_date]
(
	@database_name nvarchar(256),
	@create_date datetime
)
RETURNS datetime with schemabinding
AS
BEGIN
	RETURN (select case when @database_name = 'tempdb' then '1970-01-01' else @create_date end)
END
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE FUNCTION [dbo].[ufn_sqlwatch_get_delta_value]
(
	@value_previous real,
	@value_current real
)
RETURNS real with schemabinding
AS
BEGIN
	RETURN (select convert(real,case when @value_current > @value_previous then @value_current  - isnull(@value_previous,0) else 0 end))
END
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE FUNCTION [dbo].[ufn_sqlwatch_get_sql_version]()
RETURNS smallint with schemabinding
AS
BEGIN
    declare @return smallint,
            --ProductMajorVersion is only availabl since 2012 but its quicker to parse as it returns simple number:
            @ProductMajorVersion tinyint = convert(tinyint,SERVERPROPERTY('ProductMajorVersion'))

    return (select case
        when @ProductMajorVersion is not null then 
            case 
                when @ProductMajorVersion = 11 then 2012
                when @ProductMajorVersion = 12 then 2014
                when @ProductMajorVersion = 13 then 2016
                when @ProductMajorVersion = 14 then 2017
                when @ProductMajorVersion = 15 then 2019
                else 0000 
            end
        else
            case
                when convert(varchar(128), SERVERPROPERTY ('ProductVersion')) like '8%' then 2000
                when convert(varchar(128), SERVERPROPERTY ('ProductVersion')) like '9%' then 2005
                when convert(varchar(128), SERVERPROPERTY ('ProductVersion')) like '10.0%' then 2008
                --the 10.5 is 2008R2 but for the sake of simplicity I am going to call it 2009 so I can simply use <, >, = in my queries.
                --for example, if version < 2017 then.... If we return '2008R2' as varchar the same will not be possible.
                when convert(varchar(128), SERVERPROPERTY ('ProductVersion')) like '10.5%' then 2009
                else 0000
            end
    end)
END
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE FUNCTION [dbo].[ufn_sqlwatch_get_xes_timestamp]
(
	@event_data nvarchar(max)
)
RETURNS datetime2(0) with schemabinding
AS
BEGIN
	RETURN (select substring(@event_data,PATINDEX('%timestamp%',@event_data)+len('timestamp="'),24))
END
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE FUNCTION [dbo].[ufn_sqlwatch_parse_client_app_name] 
(
	@client_app_name varchar(255)
) 
RETURNS varchar(255) with schemabinding
AS
BEGIN
	return
		'Not Implemented'
		--case 
			--check if the passed string contains what we need. if it does, then filter out the binary string and replace it with the actual agent job name.
			--otherwise just return what was passed:
			--when @client_app_name like 'SQLAGent - TSQL JobStep%' then replace(w.client_app_name collate DATABASE_DEFAULT,left(replace(w.client_app_name collate DATABASE_DEFAULT,'SQLAgent - TSQL JobStep (Job ',''),34),j.name) 
			--else @client_app_name end
END
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE FUNCTION [dbo].[ufn_sqlwatch_parse_job_id]
(
	@client_app_name nvarchar(256)
)
RETURNS uniqueidentifier with schemabinding
AS
BEGIN
	RETURN (select convert(uniqueidentifier,case when @client_app_name like 'SQLAGent - TSQL JobStep%' then convert(varbinary,left(replace(@client_app_name collate DATABASE_DEFAULT,'SQLAgent - TSQL JobStep (Job ',''),34),1) else null end))
END
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[dbachecksChecks](
	[Group] [nvarchar](max) NULL,
	[Type] [nvarchar](max) NULL,
	[UniqueTag] [nvarchar](max) NULL,
	[AllTags] [nvarchar](max) NULL,
	[Config] [nvarchar](max) NULL,
	[Description] [nvarchar](max) NULL,
	[Describe] [nvarchar](max) NULL
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[dbachecksResults](
	[Date] [datetime2](7) NOT NULL,
	[Label] [nvarchar](255) NULL,
	[Describe] [nvarchar](255) NULL,
	[Context] [nvarchar](255) NULL,
	[Name] [nvarchar](600) NULL,
	[Database] [nvarchar](255) NULL,
	[ComputerName] [nvarchar](255) NULL,
	[Instance] [nvarchar](255) NULL,
	[Result] [nvarchar](10) NULL,
	[FailureMessage] [nvarchar](max) NULL
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_config_activated_procedures](
	[procedure_name] [nvarchar](128) NOT NULL,
	[timer_seconds] [int] NULL,
 CONSTRAINT [pk_sqlwatch_config_activated_procedures] PRIMARY KEY CLUSTERED 
(
	[procedure_name] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_config_baseline](
	[baseline_id] [smallint] IDENTITY(1,1) NOT NULL,
	[baseline_start] [datetime2](0) NOT NULL,
	[baseline_end] [datetime2](0) NOT NULL,
	[is_default] [bit] NOT NULL,
	[comments] [varchar](max) NULL,
 CONSTRAINT [pk_sqlwatch_config_baseline] PRIMARY KEY CLUSTERED 
(
	[baseline_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_config_check_template](
	[check_template_id] [smallint] IDENTITY(1,1) NOT NULL,
	[check_name] [nvarchar](255) NOT NULL,
	[check_description] [nvarchar](2048) NULL,
	[check_query] [nvarchar](max) NOT NULL,
	[check_frequency_minutes] [smallint] NULL,
	[check_threshold_warning] [varchar](100) NULL,
	[check_threshold_critical] [varchar](100) NOT NULL,
	[check_enabled] [bit] NOT NULL,
	[ignore_flapping] [bit] NOT NULL,
	[expand_by] [varchar](50) NULL,
	[user_modified] [bit] NOT NULL,
	[template_enabled] [bit] NULL,
	[use_baseline] [bit] NULL,
 CONSTRAINT [pk_sqlwatch_config_check_template] PRIMARY KEY CLUSTERED 
(
	[check_name] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_config_check_template_action](
	[check_name] [nvarchar](255) NOT NULL,
	[action_id] [smallint] NOT NULL,
	[action_every_failure] [bit] NOT NULL,
	[action_recovery] [bit] NOT NULL,
	[action_repeat_period_minutes] [smallint] NULL,
	[action_hourly_limit] [tinyint] NOT NULL,
	[action_template_id] [smallint] NOT NULL,
 CONSTRAINT [pk_sqlwatch_config_check_template_action] PRIMARY KEY CLUSTERED 
(
	[check_name] ASC,
	[action_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_config_exclude_database](
	[database_name_pattern] [nvarchar](128) NOT NULL,
	[snapshot_type_id] [tinyint] NOT NULL,
 CONSTRAINT [pk_sqlwatch_config_exclude_database] PRIMARY KEY CLUSTERED 
(
	[database_name_pattern] ASC,
	[snapshot_type_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_config_exclude_procedure](
	[database_name_pattern] [nvarchar](128) NOT NULL,
	[procedure_name_pattern] [nvarchar](256) NOT NULL,
	[snapshot_type_id] [tinyint] NOT NULL,
 CONSTRAINT [pk_sqlwatch_config_exclude_procedure] PRIMARY KEY CLUSTERED 
(
	[database_name_pattern] ASC,
	[procedure_name_pattern] ASC,
	[snapshot_type_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_config_exclude_wait_stats](
	[wait_type] [nvarchar](60) NOT NULL,
 CONSTRAINT [pk_sqlwatch_config_exclude_wait_stats] PRIMARY KEY CLUSTERED 
(
	[wait_type] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_config_exclude_xes_long_query](
	[exclusion_id] [tinyint] IDENTITY(1,1) NOT NULL,
	[statement] [varchar](8000) NULL,
	[sql_text] [varchar](8000) NULL,
	[username] [varchar](255) NULL,
	[client_hostname] [varchar](255) NULL,
	[client_app_name] [varchar](255) NULL,
 CONSTRAINT [pk_sqlwatch_config_exclude_xes_long_query] PRIMARY KEY CLUSTERED 
(
	[exclusion_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY],
 CONSTRAINT [uq_sqlwatch_config_exclude_xes_long_query] UNIQUE NONCLUSTERED 
(
	[statement] ASC,
	[sql_text] ASC,
	[username] ASC,
	[client_hostname] ASC,
	[client_app_name] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_config_include_errorlog_keywords](
	[keyword_id] [int] IDENTITY(1,1) NOT NULL,
	[keyword1] [nvarchar](255) NOT NULL,
	[keyword2] [nvarchar](255) NULL,
	[log_type_id] [int] NOT NULL,
 CONSTRAINT [pk_sqlwatch_config_include_errorlog_keywords] PRIMARY KEY CLUSTERED 
(
	[keyword_id] ASC,
	[log_type_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_config_include_index_histogram](
	[object_name_pattern] [nvarchar](128) NOT NULL,
	[index_name_pattern] [nvarchar](128) NOT NULL,
 CONSTRAINT [pk_sqlwatch_config_include_index_histogram] PRIMARY KEY CLUSTERED 
(
	[object_name_pattern] ASC,
	[index_name_pattern] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_config_performance_counters](
	[object_name] [nvarchar](128) NOT NULL,
	[instance_name] [nvarchar](128) NOT NULL,
	[counter_name] [nvarchar](128) NOT NULL,
	[base_counter_name] [nvarchar](128) NULL,
	[collect] [bit] NULL,
 CONSTRAINT [pk_sql_perf_mon_config_perf_counters] PRIMARY KEY CLUSTERED 
(
	[object_name] ASC,
	[instance_name] ASC,
	[counter_name] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_config_report_style](
	[report_style_id] [smallint] IDENTITY(1,1) NOT NULL,
	[style] [nvarchar](max) NOT NULL,
	[date_created] [datetime] NOT NULL,
	[date_updated] [datetime] NULL,
 CONSTRAINT [pk_sqlwatch_config_report_style] PRIMARY KEY CLUSTERED 
(
	[report_style_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_config_wait_stats](
	[wait_type] [nvarchar](60) NOT NULL,
	[wait_category] [nvarchar](60) NOT NULL,
	[report_include] [bit] NOT NULL,
 CONSTRAINT [pk_sqlwatch_config_wait_stats] PRIMARY KEY CLUSTERED 
(
	[wait_type] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_logger_check_action](
	[sql_instance] [varchar](32) NOT NULL,
	[snapshot_time] [datetime2](0) NOT NULL,
	[snapshot_type_id] [tinyint] NOT NULL,
	[check_id] [smallint] NOT NULL,
	[action_id] [smallint] NOT NULL,
	[action_attributes] [xml] NULL,
 CONSTRAINT [pk_sqlwatch_logger_check_action] PRIMARY KEY CLUSTERED 
(
	[snapshot_time] ASC,
	[sql_instance] ASC,
	[check_id] ASC,
	[snapshot_type_id] ASC,
	[action_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, DATA_COMPRESSION = PAGE) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_logger_dm_exec_requests_stats](
	[type] [bit] NOT NULL,
	[snapshot_time] [datetime2](0) NOT NULL,
	[snapshot_type_id] [tinyint] NOT NULL,
	[sql_instance] [varchar](32) NOT NULL,
	[background] [real] NOT NULL,
	[running] [real] NOT NULL,
	[runnable] [real] NOT NULL,
	[sleeping] [real] NOT NULL,
	[suspended] [real] NOT NULL,
	[wait_time] [real] NULL,
	[cpu_time] [real] NULL,
	[waiting_tasks] [real] NULL,
	[waiting_tasks_wait_duration_ms] [real] NULL,
 CONSTRAINT [pk_sqlwatch_logger_dm_exec_requests] PRIMARY KEY CLUSTERED 
(
	[type] ASC,
	[snapshot_time] ASC,
	[sql_instance] ASC,
	[snapshot_type_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, DATA_COMPRESSION = PAGE) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_logger_dm_exec_sessions_stats](
	[type] [bit] NOT NULL,
	[snapshot_time] [datetime2](0) NOT NULL,
	[snapshot_type_id] [tinyint] NOT NULL,
	[sql_instance] [varchar](32) NOT NULL,
	[running] [real] NOT NULL,
	[sleeping] [real] NOT NULL,
	[dormant] [real] NOT NULL,
	[preconnect] [real] NOT NULL,
	[cpu_time] [real] NOT NULL,
	[reads] [real] NOT NULL,
	[writes] [real] NOT NULL,
 CONSTRAINT [pk_sqlwatch_logger_dm_exec_sessions] PRIMARY KEY CLUSTERED 
(
	[type] ASC,
	[snapshot_time] ASC,
	[snapshot_type_id] ASC,
	[sql_instance] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, DATA_COMPRESSION = PAGE) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_logger_perf_query_stats](
	[sql_instance] [varchar](32) NOT NULL,
	[snapshot_time] [datetime2](0) NOT NULL,
	[snapshot_type_id] [tinyint] NOT NULL,
	[plan_handle] [varbinary](64) NOT NULL,
	[statement_start_offset] [int] NOT NULL,
	[statement_end_offset] [int] NOT NULL,
	[creation_time] [datetime] NOT NULL,
	[last_execution_time] [datetime] NOT NULL,
	[execution_count] [real] NULL,
	[total_worker_time] [real] NULL,
	[last_worker_time] [real] NULL,
	[min_worker_time] [real] NULL,
	[max_worker_time] [real] NULL,
	[total_physical_reads] [real] NULL,
	[last_physical_reads] [real] NULL,
	[min_physical_reads] [real] NULL,
	[max_physical_reads] [real] NULL,
	[total_logical_writes] [real] NULL,
	[last_logical_writes] [real] NULL,
	[min_logical_writes] [real] NULL,
	[max_logical_writes] [real] NULL,
	[total_logical_reads] [real] NULL,
	[last_logical_reads] [real] NULL,
	[min_logical_reads] [real] NULL,
	[max_logical_reads] [real] NULL,
	[total_elapsed_time] [real] NULL,
	[last_elapsed_time] [real] NULL,
	[min_elapsed_time] [real] NULL,
	[max_elapsed_time] [real] NULL,
	[delta_worker_time] [real] NULL,
	[delta_physical_reads] [real] NULL,
	[delta_logical_writes] [real] NULL,
	[delta_logical_reads] [real] NULL,
	[delta_elapsed_time] [real] NULL,
	[total_clr_time] [real] NULL,
	[last_clr_time] [real] NULL,
	[min_clr_time] [real] NULL,
	[max_clr_time] [real] NULL,
	[total_rows] [real] NULL,
	[last_rows] [real] NULL,
	[min_rows] [real] NULL,
	[max_rows] [real] NULL,
	[total_dop] [real] NULL,
	[last_dop] [real] NULL,
	[min_dop] [real] NULL,
	[max_dop] [real] NULL,
	[total_grant_kb] [real] NULL,
	[last_grant_kb] [real] NULL,
	[min_grant_kb] [real] NULL,
	[max_grant_kb] [real] NULL,
	[total_used_grant_kb] [real] NULL,
	[last_used_grant_kb] [real] NULL,
	[min_used_grant_kb] [real] NULL,
	[max_used_grant_kb] [real] NULL,
	[total_ideal_grant_kb] [real] NULL,
	[last_ideal_grant_kb] [real] NULL,
	[min_ideal_grant_kb] [real] NULL,
	[max_ideal_grant_kb] [real] NULL,
	[total_reserved_threads] [real] NULL,
	[last_reserved_threads] [real] NULL,
	[min_reserved_threads] [real] NULL,
	[max_reserved_threads] [real] NULL,
	[total_used_threads] [real] NULL,
	[last_used_threads] [real] NULL,
	[min_used_threads] [real] NULL,
	[max_used_threads] [real] NULL,
	[delta_time_s] [int] NULL,
 CONSTRAINT [pk_sqlwatch_logger_perf_query_stats] PRIMARY KEY CLUSTERED 
(
	[sql_instance] ASC,
	[plan_handle] ASC,
	[statement_start_offset] ASC,
	[statement_end_offset] ASC,
	[snapshot_time] ASC,
	[snapshot_type_id] ASC,
	[creation_time] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, DATA_COMPRESSION = PAGE) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_logger_report_action](
	[sql_instance] [varchar](32) NOT NULL,
	[snapshot_time] [datetime2](0) NOT NULL,
	[snapshot_type_id] [tinyint] NOT NULL,
	[report_id] [smallint] NOT NULL,
	[action_id] [smallint] NOT NULL,
 CONSTRAINT [pk_sqlwatch_logger_report_action_action] PRIMARY KEY CLUSTERED 
(
	[snapshot_time] ASC,
	[sql_instance] ASC,
	[snapshot_type_id] ASC,
	[report_id] ASC,
	[action_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, DATA_COMPRESSION = PAGE) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_logger_system_configuration](
	[sql_instance] [varchar](32) NOT NULL,
	[sqlwatch_configuration_id] [smallint] NOT NULL,
	[value] [int] NOT NULL,
	[value_in_use] [int] NOT NULL,
	[snapshot_time] [datetime2](0) NOT NULL,
	[snapshot_type_id] [tinyint] NOT NULL,
 CONSTRAINT [pk_sqlwatch_logger_system_configuration] PRIMARY KEY CLUSTERED 
(
	[sql_instance] ASC,
	[sqlwatch_configuration_id] ASC,
	[snapshot_time] ASC,
	[snapshot_type_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, DATA_COMPRESSION = PAGE) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_logger_xes_query_problems](
	[event_id] [int] IDENTITY(1,1) NOT NULL,
	[event_time] [datetime] NOT NULL,
	[event_name] [varchar](255) NOT NULL,
	[database_name] [varchar](255) NULL,
	[username] [varchar](255) NULL,
	[client_hostname] [varchar](255) NULL,
	[client_app_name] [varchar](255) NULL,
	[snapshot_time] [datetime2](0) NOT NULL,
	[snapshot_type_id] [tinyint] NOT NULL,
	[sql_instance] [varchar](32) NOT NULL,
	[problem_details] [xml] NULL,
	[event_hash] [varbinary](20) NULL,
	[occurence] [real] NULL,
 CONSTRAINT [pk_sqlwatch_logger_xes_query_problems] PRIMARY KEY NONCLUSTERED 
(
	[snapshot_time] ASC,
	[sql_instance] ASC,
	[snapshot_type_id] ASC,
	[event_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, DATA_COMPRESSION = PAGE) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_meta_performance_counter_instance](
	[performance_counter_instance_id] [int] IDENTITY(1,1) NOT NULL,
	[performance_counter_id] [smallint] NOT NULL,
	[instance_name] [nvarchar](128) NULL,
	[sql_instance] [varchar](32) NOT NULL,
	[date_updated] [datetime] NULL,
 CONSTRAINT [pk_sqlwatch_stage_performance_counters_to_collect] PRIMARY KEY CLUSTERED 
(
	[performance_counter_instance_id] ASC,
	[performance_counter_id] ASC,
	[sql_instance] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_meta_program_name](
	[program_name_id] [smallint] IDENTITY(1,1) NOT NULL,
	[program_name] [nvarchar](128) NOT NULL,
	[sql_instance] [varchar](32) NOT NULL,
 CONSTRAINT [pk_sqlwatch_meta_program_name] PRIMARY KEY CLUSTERED 
(
	[program_name] ASC,
	[sql_instance] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_meta_report](
	[sql_instance] [varchar](32) NOT NULL,
	[report_id] [smallint] NOT NULL,
	[report_title] [varchar](255) NOT NULL,
	[report_description] [varchar](4000) NULL,
	[report_definition] [nvarchar](max) NOT NULL,
	[report_definition_type] [varchar](25) NOT NULL,
	[report_last_run_date] [datetime] NULL,
	[report_batch_id] [varchar](255) NULL,
	[date_updated] [datetime] NOT NULL,
 CONSTRAINT [pk_sqlwatch_meta_report] PRIMARY KEY CLUSTERED 
(
	[sql_instance] ASC,
	[report_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_meta_repository_import_queue](
	[sql_instance] [varchar](32) NOT NULL,
	[object_name] [nvarchar](512) NOT NULL,
	[time_queued] [datetime2](7) NOT NULL,
	[import_batch_id] [uniqueidentifier] NOT NULL,
	[parent_object_name] [nvarchar](512) NULL,
	[priority] [tinyint] NOT NULL,
	[load_type] [char](1) NOT NULL,
	[import_status] [varchar](50) NULL,
	[import_start_time] [datetime2](7) NULL,
	[import_end_time] [datetime2](7) NULL,
 CONSTRAINT [pk_sqlwatch_repository_import_queue] PRIMARY KEY CLUSTERED 
(
	[sql_instance] ASC,
	[object_name] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_meta_repository_import_thread](
	[thread_name] [varchar](128) NOT NULL,
	[thread_start_time] [datetime2](7) NOT NULL,
 CONSTRAINT [pk_sqlwatch_meta_repository_import_thread] PRIMARY KEY CLUSTERED 
(
	[thread_name] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_meta_sql_query](
	[sql_instance] [varchar](32) NOT NULL,
	[sqlwatch_query_hash] [varbinary](16) NOT NULL,
	[sql_text] [nvarchar](max) NULL,
	[date_first_seen] [datetime] NOT NULL,
	[date_last_seen] [datetime] NOT NULL,
 CONSTRAINT [pk_sqlwatch_meta_sql_text] PRIMARY KEY CLUSTERED 
(
	[sql_instance] ASC,
	[sqlwatch_query_hash] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_meta_system_configuration](
	[sqlwatch_configuration_id] [smallint] IDENTITY(1,1) NOT NULL,
	[sql_instance] [varchar](32) NOT NULL,
	[configuration_id] [int] NOT NULL,
	[name] [nvarchar](128) NOT NULL,
	[description] [nvarchar](512) NOT NULL,
	[value] [int] NOT NULL,
	[value_in_use] [int] NULL,
	[date_created] [datetime] NOT NULL,
	[date_updated] [datetime] NOT NULL,
	[is_record_deleted] [bit] NULL,
 CONSTRAINT [pk_sqlwatch_meta_system_configuration] PRIMARY KEY CLUSTERED 
(
	[sql_instance] ASC,
	[sqlwatch_configuration_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_meta_system_configuration_scd](
	[sql_instance] [varchar](32) NOT NULL,
	[sqlwatch_configuration_id] [smallint] NOT NULL,
	[value] [int] NOT NULL,
	[value_in_use] [int] NOT NULL,
	[valid_from] [datetime] NOT NULL,
	[valid_until] [datetime] NULL,
	[date_updated] [datetime] NOT NULL,
 CONSTRAINT [pk_sqlwatch_logger_system_configuration_scd] PRIMARY KEY CLUSTERED 
(
	[sqlwatch_configuration_id] ASC,
	[sql_instance] ASC,
	[valid_from] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_stage_perf_os_wait_stats](
	[wait_type] [nvarchar](60) NOT NULL,
	[waiting_tasks_count] [bigint] NOT NULL,
	[wait_time_ms] [bigint] NOT NULL,
	[max_wait_time_ms] [bigint] NOT NULL,
	[signal_wait_time_ms] [bigint] NOT NULL,
	[snapshot_time] [datetime2](0) NOT NULL,
	[wait_type_id] [smallint] NOT NULL,
 CONSTRAINT [pk_sqlwatch_stage_perf_os_wait_stats] PRIMARY KEY CLUSTERED 
(
	[snapshot_time] ASC,
	[wait_type_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_stage_repository_tables_to_import](
	[table_name] [nvarchar](512) NOT NULL,
	[dependency_level] [tinyint] NULL,
	[has_last_seen] [bit] NULL,
	[has_last_updated] [bit] NULL,
	[has_identity] [bit] NULL,
	[primary_key] [nvarchar](max) NULL,
	[joins] [nvarchar](max) NULL,
	[updatecolumns] [nvarchar](max) NULL,
	[allcolumns] [nvarchar](max) NULL,
PRIMARY KEY CLUSTERED 
(
	[table_name] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[sqlwatch_stage_ring_buffer](
	[snapshot_time] [datetime2](0) NOT NULL,
	[percent_processor_time] [int] NULL,
	[percent_idle_time] [int] NULL,
 CONSTRAINT [pk_sqlwatch_stage_logger_ring_buffer] PRIMARY KEY CLUSTERED 
(
	[snapshot_time] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE VIEW [dbo].[vw_sqlwatch_internal_table_snapshot] with schemabinding
as

select [table_name] = 'sqlwatch_logger_agent_job_history', [snapshot_type_id] = 16
union all
select [table_name] = 'sqlwatch_logger_disk_utilisation_database', [snapshot_type_id] = 2
union all
select [table_name] = 'sqlwatch_logger_disk_utilisation_volume', [snapshot_type_id] = 17
union all
select [table_name] = 'sqlwatch_logger_index_missing_stats', [snapshot_type_id] = 3
union all
select [table_name] = 'sqlwatch_logger_index_usage_stats', [snapshot_type_id] = 14
union all
select [table_name] = 'sqlwatch_logger_index_histogram', [snapshot_type_id] = 15
union all
select [table_name] = 'sqlwatch_logger_perf_file_stats', [snapshot_type_id] = 1
union all
select [table_name] = 'sqlwatch_logger_perf_os_memory_clerks', [snapshot_type_id] = 1
union all
select [table_name] = 'sqlwatch_logger_perf_os_performance_counters', [snapshot_type_id] = 1
union all
select [table_name] = 'sqlwatch_logger_perf_os_process_memory', [snapshot_type_id] = 1
union all
select [table_name] = 'sqlwatch_logger_perf_os_schedulers', [snapshot_type_id] = 1
union all
select [table_name] = 'sqlwatch_logger_perf_os_wait_stats', [snapshot_type_id] = 1
union all
select [table_name] = 'sqlwatch_logger_whoisactive', [snapshot_type_id] = 11
union all
select [table_name] = 'sqlwatch_logger_xes_blockers', [snapshot_type_id] = 9
union all
select [table_name] = 'sqlwatch_logger_xes_iosubsystem', [snapshot_type_id] = 10
union all
select [table_name] = 'sqlwatch_logger_xes_long_queries', [snapshot_type_id] = 7
union all
select [table_name] = 'sqlwatch_logger_xes_query_processing',[snapshot_type_id] = 10
union all
select [table_name] = 'sqlwatch_logger_xes_waits_stats', [snapshot_type_id] = 6
union all
select [table_name] = 'sqlwatch_logger_check', [snapshot_type_id] = 18
union all
select [table_name] = 'sqlwatch_logger_check_action', [snapshot_type_id] = 18
union all
select [table_name] = 'sqlwatch_logger_disk_utilisation_table', [snapshot_type_id] = 22
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE VIEW [dbo].[vw_sqlwatch_sys_configurations]
as

SELECT sql_instance = @@SERVERNAME
     , configuration_id
     , name
     , CAST(value AS INT) as [value]
     , CAST(value_in_use AS INT) as [value_in_use]
     , description
  FROM sys.configurations
UNION ALL
SELECT @@SERVERNAME
    , -1
    , 'version'
    , (SELECT CAST(REPLACE(CAST(SERVERPROPERTY('productversion') AS VARCHAR(64)), '.', '') AS INT))
    , (SELECT CAST(REPLACE(CAST(SERVERPROPERTY('productversion') AS VARCHAR(64)), '.', '') AS INT))
    , 'Version as integer: xx.x.xxxx.xx'
--UNION ALL
--SELECT sql_instance = @@SERVERNAME
--     , -2
--     , 'instant file initialization'
--     , CASE WHEN s.instant_file_initialization_enabled = 'Y' THEN 1 ELSE 0 END AS instant_file_initialization_enabled
--     , CASE WHEN s.instant_file_initialization_enabled = 'Y' THEN 1 ELSE 0 END AS instant_file_initialization_enabled 
--     , 'indicates if instant file initialization (perform volume maintenance tasks) is enabled for the SQL Server service account.'
--  FROM sys.dm_server_services s
-- WHERE servicename NOT LIKE 'SQL Server Agent%' AND servicename NOT LIKE 'SQL Server Launchpad%'
GO
SET ANSI_PADDING ON
GO

CREATE NONCLUSTERED INDEX [idx_sqlwatch_dbachecks_failure_by_date] ON [dbo].[dbachecksResults]
(
	[Result] ASC,
	[Date] ASC
)
WHERE ([Result]='Failed')
WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
GO
SET ANSI_PADDING ON
GO

CREATE NONCLUSTERED INDEX [idx_sqlwatch_logger_log_message_type] ON [dbo].[sqlwatch_app_log]
(
	[process_message_type] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
GO
SET ANSI_PADDING ON
GO

CREATE UNIQUE NONCLUSTERED INDEX [idx_sqlwatch_sys_config_name] ON [dbo].[sqlwatch_config]
(
	[config_name] ASC
)
INCLUDE([config_value]) WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, IGNORE_DUP_KEY = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
GO

CREATE UNIQUE NONCLUSTERED INDEX [idx_sqlwatch_config_baseline_dates] ON [dbo].[sqlwatch_config_baseline]
(
	[baseline_start] ASC,
	[baseline_end] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, IGNORE_DUP_KEY = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
GO

CREATE UNIQUE NONCLUSTERED INDEX [idx_sqlwatch_config_baseline_default] ON [dbo].[sqlwatch_config_baseline]
(
	[is_default] ASC
)
WHERE ([is_default]=(1))
WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, IGNORE_DUP_KEY = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
GO
SET ANSI_PADDING ON
GO

CREATE UNIQUE NONCLUSTERED INDEX [idx_sqlwatch_config_include_errorlog_keywords_keyword1] ON [dbo].[sqlwatch_config_include_errorlog_keywords]
(
	[keyword1] ASC
)
WHERE ([keyword2] IS NULL)
WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, IGNORE_DUP_KEY = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
GO
SET ANSI_PADDING ON
GO

CREATE UNIQUE NONCLUSTERED INDEX [idx_sqlwatch_config_include_errorlog_keywords_keyword2] ON [dbo].[sqlwatch_config_include_errorlog_keywords]
(
	[keyword1] ASC,
	[keyword2] ASC
)
WHERE ([keyword2] IS NOT NULL)
WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, IGNORE_DUP_KEY = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
GO

CREATE NONCLUSTERED INDEX [idx_sql_perf_mon_perf_counters_types] ON [dbo].[sqlwatch_config_performance_counters]
(
	[collect] ASC
)
INCLUDE([object_name],[instance_name],[counter_name],[base_counter_name]) WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
GO

CREATE NONCLUSTERED INDEX [idx_sqlwatch_logger_agent_job_history_run_date] ON [dbo].[sqlwatch_logger_agent_job_history]
(
	[run_date] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, DATA_COMPRESSION = PAGE) ON [PRIMARY]
GO

CREATE NONCLUSTERED INDEX [idx_sqlwatch_logger_agent_job_history_run_date_utc] ON [dbo].[sqlwatch_logger_agent_job_history]
(
	[run_date_utc] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, DATA_COMPRESSION = PAGE) ON [PRIMARY]
GO

CREATE NONCLUSTERED INDEX [idx_sqlwatch_logger_check_1] ON [dbo].[sqlwatch_logger_check]
(
	[status_change] ASC
)
INCLUDE([check_status]) WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, DATA_COMPRESSION = PAGE) ON [PRIMARY]
GO
SET ANSI_PADDING ON
GO

CREATE NONCLUSTERED INDEX [idx_sqlwatch_logger_check_2] ON [dbo].[sqlwatch_logger_check]
(
	[sql_instance] ASC,
	[check_id] ASC
)
INCLUDE([snapshot_time],[snapshot_type_id]) WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, DATA_COMPRESSION = PAGE) ON [PRIMARY]
GO

CREATE NONCLUSTERED INDEX [idx_sqlwatch_logger_check_3] ON [dbo].[sqlwatch_logger_check]
(
	[check_id] ASC
)
INCLUDE([sql_instance],[snapshot_time],[snapshot_type_id],[check_value]) WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, DATA_COMPRESSION = PAGE) ON [PRIMARY]
GO
SET ANSI_PADDING ON
GO

CREATE NONCLUSTERED INDEX [idx_sqlwatch_logger_errorlog_1] ON [dbo].[sqlwatch_logger_errorlog]
(
	[keyword_id] ASC,
	[log_type_id] ASC,
	[sql_instance] ASC
)
INCLUDE([log_date]) WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, DATA_COMPRESSION = PAGE) ON [PRIMARY]
GO
SET ANSI_PADDING ON
GO

CREATE NONCLUSTERED INDEX [idx_sqlwatch_perf_counters_id_cntrl_values] ON [dbo].[sqlwatch_logger_perf_os_performance_counters]
(
	[performance_counter_id] ASC,
	[sql_instance] ASC
)
INCLUDE([cntr_value],[cntr_value_calculated]) WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, DATA_COMPRESSION = PAGE) ON [PRIMARY]
GO
SET ANSI_PADDING ON
GO

CREATE NONCLUSTERED INDEX [idx_sqlwatch_logger_perf_os_wait_stats_wait_type_id] ON [dbo].[sqlwatch_logger_perf_os_wait_stats]
(
	[sql_instance] ASC,
	[wait_type_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, DATA_COMPRESSION = PAGE) ON [PRIMARY]
GO

CREATE NONCLUSTERED INDEX [idx_sqlwatch_logger_perf_os_wait_stats_waiting_count_delta] ON [dbo].[sqlwatch_logger_perf_os_wait_stats]
(
	[waiting_tasks_count_delta] ASC
)
INCLUDE([wait_time_ms_delta]) WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, DATA_COMPRESSION = PAGE) ON [PRIMARY]
GO

CREATE NONCLUSTERED INDEX [idx_sqlwatch_logger_snapshot_header_report_time] ON [dbo].[sqlwatch_logger_snapshot_header]
(
	[report_time] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, DATA_COMPRESSION = PAGE) ON [PRIMARY]
GO

CREATE NONCLUSTERED INDEX [idx_sqlwatch_logger_snapshot_header_type_id] ON [dbo].[sqlwatch_logger_snapshot_header]
(
	[snapshot_type_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, DATA_COMPRESSION = PAGE) ON [PRIMARY]
GO

CREATE NONCLUSTERED INDEX [idx_sqlwatch_logger_xes_blockers_1] ON [dbo].[sqlwatch_logger_xes_blockers]
(
	[monitor_loop] ASC,
	[blocking_ecid] ASC,
	[blocking_spid] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, DATA_COMPRESSION = PAGE) ON [PRIMARY]
GO
SET ANSI_PADDING ON
GO

CREATE UNIQUE NONCLUSTERED INDEX [idx_sqlwatch_xes_iosubsystem_event_time] ON [dbo].[sqlwatch_logger_xes_iosubsystem]
(
	[event_time] ASC,
	[sql_instance] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, IGNORE_DUP_KEY = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, DATA_COMPRESSION = PAGE) ON [PRIMARY]
GO
SET ANSI_PADDING ON
GO

CREATE UNIQUE NONCLUSTERED INDEX [idx_sqlwatch_logger_xes_query_problems_1] ON [dbo].[sqlwatch_logger_xes_query_problems]
(
	[event_time] ASC,
	[event_name] ASC,
	[event_hash] ASC,
	[occurence] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, IGNORE_DUP_KEY = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, DATA_COMPRESSION = PAGE) ON [PRIMARY]
GO
SET ANSI_PADDING ON
GO

CREATE UNIQUE NONCLUSTERED INDEX [idx_sqlwatch_xes_query_processing_event_time] ON [dbo].[sqlwatch_logger_xes_query_processing]
(
	[event_time] ASC,
	[sql_instance] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, IGNORE_DUP_KEY = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, DATA_COMPRESSION = PAGE) ON [PRIMARY]
GO
SET ANSI_PADDING ON
GO

CREATE UNIQUE NONCLUSTERED INDEX [idx_sqlwatch_meta_baseline_dates] ON [dbo].[sqlwatch_meta_baseline]
(
	[baseline_start] ASC,
	[baseline_end] ASC,
	[sql_instance] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, IGNORE_DUP_KEY = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
GO
SET ANSI_PADDING ON
GO

CREATE UNIQUE NONCLUSTERED INDEX [idx_sqlwatch_meta_retention_default] ON [dbo].[sqlwatch_meta_baseline]
(
	[sql_instance] ASC,
	[is_default] ASC
)
WHERE ([is_default]=(1))
WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, IGNORE_DUP_KEY = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
GO

CREATE NONCLUSTERED INDEX [idx_sqlwatch_meta_check_1] ON [dbo].[sqlwatch_meta_check]
(
	[date_updated] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
GO
SET ANSI_PADDING ON
GO

CREATE NONCLUSTERED INDEX [idx_sqlwatch_meta_check_2] ON [dbo].[sqlwatch_meta_check]
(
	[target_sql_instance] ASC
)
INCLUDE([check_id]) WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
GO

CREATE NONCLUSTERED INDEX [idx_sqlwatch_meta_errorlog_attribute_1] ON [dbo].[sqlwatch_meta_errorlog_attribute]
(
	[date_updated] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
GO
SET ANSI_PADDING ON
GO

CREATE NONCLUSTERED INDEX [idx_sqlwatch_meta_errorlog_keyword_1] ON [dbo].[sqlwatch_meta_errorlog_keyword]
(
	[keyword1] ASC
)
INCLUDE([sql_instance],[keyword_id],[log_type_id]) WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
GO

CREATE NONCLUSTERED INDEX [idx_sqlwatch_meta_errorlog_keyword_2] ON [dbo].[sqlwatch_meta_errorlog_keyword]
(
	[date_updated] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
GO

CREATE NONCLUSTERED INDEX [idx_sqlwatch_meta_errorlog_text_1] ON [dbo].[sqlwatch_meta_errorlog_text]
(
	[date_updated] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
GO

CREATE NONCLUSTERED INDEX [idx_sqlwatch_meta_memory_clerk_1] ON [dbo].[sqlwatch_meta_memory_clerk]
(
	[date_updated] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
GO

CREATE NONCLUSTERED INDEX [idx_sqlwatch_meta_performance_counter_1] ON [dbo].[sqlwatch_meta_performance_counter]
(
	[date_updated] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
GO
SET ANSI_PADDING ON
GO

CREATE UNIQUE NONCLUSTERED INDEX [idx_sqlwatch_meta_procedure_1] ON [dbo].[sqlwatch_meta_procedure]
(
	[sql_instance] ASC,
	[sqlwatch_database_id] ASC,
	[procedure_name] ASC
)
INCLUDE([date_last_seen],[sqlwatch_procedure_id]) WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, IGNORE_DUP_KEY = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
GO

CREATE UNIQUE NONCLUSTERED INDEX [idx_sqlwatch_meta_program_name_id] ON [dbo].[sqlwatch_meta_program_name]
(
	[program_name_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, IGNORE_DUP_KEY = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
GO

CREATE NONCLUSTERED INDEX [idx_sqlwatch_meta_report_1] ON [dbo].[sqlwatch_meta_report]
(
	[date_updated] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
GO

CREATE NONCLUSTERED INDEX [idx_sqlwatch_meta_server_1] ON [dbo].[sqlwatch_meta_server]
(
	[date_updated] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
GO
SET ARITHABORT ON
SET CONCAT_NULL_YIELDS_NULL ON
SET QUOTED_IDENTIFIER ON
SET ANSI_NULLS ON
SET ANSI_PADDING ON
SET ANSI_WARNINGS ON
SET NUMERIC_ROUNDABORT OFF
GO

CREATE NONCLUSTERED INDEX [idx_sqlwatch_meta_server_2] ON [dbo].[sqlwatch_meta_server]
(
	[sql_instance] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
GO

CREATE NONCLUSTERED INDEX [idx_sqlwatch_meta_system_configuration_1] ON [dbo].[sqlwatch_meta_system_configuration]
(
	[date_updated] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
GO

CREATE NONCLUSTERED INDEX [idx_sqlwatch_meta_system_configuration_scd_1] ON [dbo].[sqlwatch_meta_system_configuration_scd]
(
	[date_updated] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
GO

CREATE NONCLUSTERED INDEX [idx_sqlwatch_meta_wait_stats_1] ON [dbo].[sqlwatch_meta_wait_stats]
(
	[date_updated] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
GO

CREATE NONCLUSTERED INDEX [idx_sqlwatch_meta_wait_stats_2] ON [dbo].[sqlwatch_meta_wait_stats]
(
	[is_excluded] ASC
)
INCLUDE([wait_type],[wait_type_id]) WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
GO

CREATE NONCLUSTERED INDEX [idx_sqlwatch_trend_perf_os_performance_counters_interval_minutes] ON [dbo].[sqlwatch_trend_perf_os_performance_counters]
(
	[interval_minutes] ASC
)
INCLUDE([performance_counter_id],[instance_name],[sql_instance],[snapshot_time]) WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, DATA_COMPRESSION = PAGE) ON [PRIMARY]
GO
SET ANSI_PADDING ON
GO

CREATE NONCLUSTERED INDEX [idx_sqlwatch_trend_perf_os_performance_counters_perf_counter] ON [dbo].[sqlwatch_trend_perf_os_performance_counters]
(
	[performance_counter_id] ASC,
	[sql_instance] ASC
)
INCLUDE([cntr_value_calculated_avg],[snapshot_time_offset]) WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, DATA_COMPRESSION = PAGE) ON [PRIMARY]
GO
SET ANSI_PADDING ON
GO

CREATE NONCLUSTERED INDEX [idx_sqlwatch_trend_perf_os_performance_counters_sql_instance] ON [dbo].[sqlwatch_trend_perf_os_performance_counters]
(
	[sql_instance] ASC
)
INCLUDE([performance_counter_id],[cntr_value_calculated_avg],[snapshot_time_offset]) WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, DATA_COMPRESSION = PAGE) ON [PRIMARY]
GO

CREATE NONCLUSTERED INDEX [idx_sqlwatch_trend_perf_os_performance_counters_valid_until] ON [dbo].[sqlwatch_trend_perf_os_performance_counters]
(
	[valid_until] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, DATA_COMPRESSION = PAGE) ON [PRIMARY]
GO
SET ANSI_PADDING ON
GO

CREATE NONCLUSTERED INDEX [idx_sqlwatch_trend_perf_os_performance_counters_value] ON [dbo].[sqlwatch_trend_perf_os_performance_counters]
(
	[performance_counter_id] ASC,
	[sql_instance] ASC,
	[interval_minutes] ASC
)
INCLUDE([cntr_value_calculated_avg]) WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, DATA_COMPRESSION = PAGE) ON [PRIMARY]
GO
ALTER TABLE [dbo].[sqlwatch_app_log] ADD  CONSTRAINT [df_sqlwatch_logger_sql_instance]  DEFAULT (@@servername) FOR [sql_instance]
GO
ALTER TABLE [dbo].[sqlwatch_app_log] ADD  CONSTRAINT [df_sqlwatch_logger_log_time]  DEFAULT (sysdatetime()) FOR [event_time]
GO
ALTER TABLE [dbo].[sqlwatch_config_action] ADD  DEFAULT ((1)) FOR [action_enabled]
GO
ALTER TABLE [dbo].[sqlwatch_config_action] ADD  CONSTRAINT [df_sqlwatch_config_action_date_created]  DEFAULT (getdate()) FOR [date_created]
GO
ALTER TABLE [dbo].[sqlwatch_config_check] ADD  DEFAULT ((1)) FOR [check_enabled]
GO
ALTER TABLE [dbo].[sqlwatch_config_check] ADD  DEFAULT ((1)) FOR [use_baseline]
GO
ALTER TABLE [dbo].[sqlwatch_config_check] ADD  CONSTRAINT [df_sqlwatch_config_check_date_created]  DEFAULT (getdate()) FOR [date_created]
GO
ALTER TABLE [dbo].[sqlwatch_config_check] ADD  CONSTRAINT [df_sqlwatch_config_check_flapping]  DEFAULT ((0)) FOR [ignore_flapping]
GO
ALTER TABLE [dbo].[sqlwatch_config_check] ADD  CONSTRAINT [df_sqlwatch_config_check_target_sql_instace]  DEFAULT (@@servername) FOR [target_sql_instance]
GO
ALTER TABLE [dbo].[sqlwatch_config_check_action] ADD  CONSTRAINT [df_sqlwatch_config_check_action_every_failure]  DEFAULT ((0)) FOR [action_every_failure]
GO
ALTER TABLE [dbo].[sqlwatch_config_check_action] ADD  CONSTRAINT [df_sqlwatch_config_check_action_recovery]  DEFAULT ((1)) FOR [action_recovery]
GO
ALTER TABLE [dbo].[sqlwatch_config_check_action] ADD  CONSTRAINT [df_sqlwatch_config_check_action_hourly_limit]  DEFAULT ((2)) FOR [action_hourly_limit]
GO
ALTER TABLE [dbo].[sqlwatch_config_check_action] ADD  CONSTRAINT [df_sqlwatch_config_check_action_date_created]  DEFAULT (getutcdate()) FOR [date_created]
GO
ALTER TABLE [dbo].[sqlwatch_config_check_action_template] ADD  CONSTRAINT [df_sqlwatch_config_check_action_template_date_added]  DEFAULT (getdate()) FOR [date_created]
GO
ALTER TABLE [dbo].[sqlwatch_config_check_action_template] ADD  CONSTRAINT [df_sqlwatch_config_check_action_template_type]  DEFAULT ('TEXT') FOR [action_template_type]
GO
ALTER TABLE [dbo].[sqlwatch_config_check_template] ADD  DEFAULT ((1)) FOR [check_enabled]
GO
ALTER TABLE [dbo].[sqlwatch_config_check_template] ADD  CONSTRAINT [df_sqlwatch_config_check_template_flapping]  DEFAULT ((0)) FOR [ignore_flapping]
GO
ALTER TABLE [dbo].[sqlwatch_config_report] ADD  CONSTRAINT [df_sqlwatch_config_report_type]  DEFAULT ('HTML-Table') FOR [report_definition_type]
GO
ALTER TABLE [dbo].[sqlwatch_config_report] ADD  CONSTRAINT [df_sqlwatch_config_report_active]  DEFAULT ((1)) FOR [report_active]
GO
ALTER TABLE [dbo].[sqlwatch_config_report] ADD  CONSTRAINT [df_sqlwatch_config_report_date_created]  DEFAULT (getdate()) FOR [date_created]
GO
ALTER TABLE [dbo].[sqlwatch_config_report_style] ADD  CONSTRAINT [df_sqlwatch_config_report_style_date_created]  DEFAULT (getdate()) FOR [date_created]
GO
ALTER TABLE [dbo].[sqlwatch_config_snapshot_type] ADD  CONSTRAINT [df_sqlwatch_config_snapshot_type_collection]  DEFAULT ((1)) FOR [collect]
GO
ALTER TABLE [dbo].[sqlwatch_config_sql_instance] ADD  CONSTRAINT [df_sqlwatch_config_sql_instance_sql_instance]  DEFAULT (@@servername) FOR [sql_instance]
GO
ALTER TABLE [dbo].[sqlwatch_config_sql_instance] ADD  CONSTRAINT [df_sqlwatch_config_sql_instance_database_name]  DEFAULT (db_name()) FOR [sqlwatch_database_name]
GO
ALTER TABLE [dbo].[sqlwatch_config_sql_instance] ADD  CONSTRAINT [df_sqlwatch_config_sql_instance_env]  DEFAULT ('DEFAULT') FOR [environment]
GO
ALTER TABLE [dbo].[sqlwatch_config_sql_instance] ADD  CONSTRAINT [df_sqlwatch_config_sql_instance]  DEFAULT ((1)) FOR [repo_collector_is_active]
GO
ALTER TABLE [dbo].[sqlwatch_logger_agent_job_history] ADD  CONSTRAINT [df_sqlwatch_logger_agent_job_history_run_date_utc]  DEFAULT ('1970-01-01') FOR [run_date_utc]
GO
ALTER TABLE [dbo].[sqlwatch_logger_check] ADD  CONSTRAINT [df_sqlwatch_logger_check_type]  DEFAULT ((18)) FOR [snapshot_type_id]
GO
ALTER TABLE [dbo].[sqlwatch_logger_check_action] ADD  CONSTRAINT [df_sqlwatch_logger_check_action_type]  DEFAULT ((18)) FOR [snapshot_type_id]
GO
ALTER TABLE [dbo].[sqlwatch_logger_disk_utilisation_database] ADD  CONSTRAINT [df_sqlwatch_logger_disk_utilisation_database_sql_instance]  DEFAULT (@@servername) FOR [sql_instance]
GO
ALTER TABLE [dbo].[sqlwatch_logger_disk_utilisation_volume] ADD  CONSTRAINT [df_sqlwatch_logger_disk_utilisation_volume_sql_instance]  DEFAULT (@@servername) FOR [sql_instance]
GO
ALTER TABLE [dbo].[sqlwatch_logger_errorlog] ADD  DEFAULT (@@servername) FOR [sql_instance]
GO
ALTER TABLE [dbo].[sqlwatch_logger_index_histogram] ADD  CONSTRAINT [df_sqlwatch_logger_index_usage_stats_histogram_sql_instance]  DEFAULT (@@servername) FOR [sql_instance]
GO
ALTER TABLE [dbo].[sqlwatch_logger_index_missing_stats] ADD  CONSTRAINT [df_sqlwatch_logger_index_missing_stats_sql_instance]  DEFAULT (@@servername) FOR [sql_instance]
GO
ALTER TABLE [dbo].[sqlwatch_logger_index_usage_stats] ADD  CONSTRAINT [df_sqlwatch_logger_index_usage_stats_sql_instance]  DEFAULT (@@servername) FOR [sql_instance]
GO
ALTER TABLE [dbo].[sqlwatch_logger_index_usage_stats] ADD  DEFAULT ((0)) FOR [partition_id]
GO
ALTER TABLE [dbo].[sqlwatch_logger_perf_file_stats] ADD  CONSTRAINT [df_sqlwatch_logger_perf_file_stats_type]  DEFAULT ((1)) FOR [snapshot_type_id]
GO
ALTER TABLE [dbo].[sqlwatch_logger_perf_file_stats] ADD  CONSTRAINT [df_sqlwatch_logger_perf_file_stats_sql_instance]  DEFAULT (@@servername) FOR [sql_instance]
GO
ALTER TABLE [dbo].[sqlwatch_logger_perf_os_memory_clerks] ADD  CONSTRAINT [df_sqlwatch_logger_perf_os_memory_clerks_type]  DEFAULT ((1)) FOR [snapshot_type_id]
GO
ALTER TABLE [dbo].[sqlwatch_logger_perf_os_memory_clerks] ADD  CONSTRAINT [df_sqlwatch_logger_perf_os_memory_clerks_sql_instance]  DEFAULT (@@servername) FOR [sql_instance]
GO
ALTER TABLE [dbo].[sqlwatch_logger_perf_os_performance_counters] ADD  CONSTRAINT [df_sqlwatch_logger_perf_os_performance_counters_type]  DEFAULT ((1)) FOR [snapshot_type_id]
GO
ALTER TABLE [dbo].[sqlwatch_logger_perf_os_performance_counters] ADD  CONSTRAINT [df_sqlwatch_logger_perf_os_performance_counters_sql_instance]  DEFAULT (@@servername) FOR [sql_instance]
GO
ALTER TABLE [dbo].[sqlwatch_logger_perf_os_process_memory] ADD  CONSTRAINT [df_sqlwatch_logger_perf_os_process_memory_type]  DEFAULT ((1)) FOR [snapshot_type_id]
GO
ALTER TABLE [dbo].[sqlwatch_logger_perf_os_process_memory] ADD  CONSTRAINT [df_sqlwatch_logger_perf_os_process_memory_sql_instance]  DEFAULT (@@servername) FOR [sql_instance]
GO
ALTER TABLE [dbo].[sqlwatch_logger_perf_os_schedulers] ADD  CONSTRAINT [df_sqlwatch_logger_perf_os_schedulers_type]  DEFAULT ((1)) FOR [snapshot_type_id]
GO
ALTER TABLE [dbo].[sqlwatch_logger_perf_os_schedulers] ADD  CONSTRAINT [df_sqlwatch_logger_perf_os_schedulers_sql_instance]  DEFAULT (@@servername) FOR [sql_instance]
GO
ALTER TABLE [dbo].[sqlwatch_logger_perf_os_wait_stats] ADD  CONSTRAINT [df_sqlwatch_logger_perf_os_wait_stats_type]  DEFAULT ((1)) FOR [snapshot_type_id]
GO
ALTER TABLE [dbo].[sqlwatch_logger_perf_os_wait_stats] ADD  CONSTRAINT [df_sqlwatch_logger_perf_os_wait_stats_sql_instance]  DEFAULT (@@servername) FOR [sql_instance]
GO
ALTER TABLE [dbo].[sqlwatch_logger_report_action] ADD  CONSTRAINT [df_sqlwatch_logger_report_action_type]  DEFAULT ((18)) FOR [snapshot_type_id]
GO
ALTER TABLE [dbo].[sqlwatch_logger_snapshot_header] ADD  CONSTRAINT [df_sqlwatch_logger_snapshot_header_sql_instance]  DEFAULT (@@servername) FOR [sql_instance]
GO
ALTER TABLE [dbo].[sqlwatch_logger_snapshot_header] ADD  CONSTRAINT [df_sqlwatch_logger_snapshot_header_time_offset]  DEFAULT (datepart(tzoffset,sysdatetimeoffset())) FOR [snapshot_time_utc_offset]
GO
ALTER TABLE [dbo].[sqlwatch_logger_system_configuration] ADD  DEFAULT (@@servername) FOR [sql_instance]
GO
ALTER TABLE [dbo].[sqlwatch_logger_system_configuration] ADD  DEFAULT ((26)) FOR [snapshot_type_id]
GO
ALTER TABLE [dbo].[sqlwatch_logger_whoisactive] ADD  CONSTRAINT [df_sqlwatch_logger_whoisactive_type]  DEFAULT ((1)) FOR [snapshot_type_id]
GO
ALTER TABLE [dbo].[sqlwatch_logger_whoisactive] ADD  CONSTRAINT [df_sqlwatch_logger_whoisactive_sql_instance]  DEFAULT (@@servername) FOR [sql_instance]
GO
ALTER TABLE [dbo].[sqlwatch_logger_xes_iosubsystem] ADD  CONSTRAINT [df_sqlwatch_logger_xes_iosubsystem_type]  DEFAULT ((1)) FOR [snapshot_type_id]
GO
ALTER TABLE [dbo].[sqlwatch_logger_xes_iosubsystem] ADD  CONSTRAINT [df_sqlwatch_logger_xes_iosubsystem_sql_instance]  DEFAULT (@@servername) FOR [sql_instance]
GO
ALTER TABLE [dbo].[sqlwatch_logger_xes_long_queries] ADD  CONSTRAINT [df_sqlwatch_logger_xes_long_queries_type]  DEFAULT ((7)) FOR [snapshot_type_id]
GO
ALTER TABLE [dbo].[sqlwatch_logger_xes_long_queries] ADD  CONSTRAINT [df_sqlwatch_logger_xes_long_queries_sql_instance]  DEFAULT (@@servername) FOR [sql_instance]
GO
ALTER TABLE [dbo].[sqlwatch_logger_xes_query_processing] ADD  CONSTRAINT [df_sqlwatch_logger_xes_query_processing_type]  DEFAULT ((1)) FOR [snapshot_type_id]
GO
ALTER TABLE [dbo].[sqlwatch_logger_xes_query_processing] ADD  CONSTRAINT [df_sqlwatch_logger_xes_query_processing_sql_instance]  DEFAULT (@@servername) FOR [sql_instance]
GO
ALTER TABLE [dbo].[sqlwatch_meta_action_queue] ADD  CONSTRAINT [df_sqlwatch_meta_action_queue_time_queued]  DEFAULT (sysdatetime()) FOR [time_queued]
GO
ALTER TABLE [dbo].[sqlwatch_meta_agent_job] ADD  CONSTRAINT [df_sqlwatch_meta_agent_job_last_seen]  DEFAULT (getutcdate()) FOR [date_last_seen]
GO
ALTER TABLE [dbo].[sqlwatch_meta_agent_job_step] ADD  CONSTRAINT [df_sqlwatch_meta_agent_job_step_last_seen]  DEFAULT (getutcdate()) FOR [date_last_seen]
GO
ALTER TABLE [dbo].[sqlwatch_meta_check] ADD  CONSTRAINT [df_sqlwatch_meta_check_sql_instance]  DEFAULT (@@servername) FOR [sql_instance]
GO
ALTER TABLE [dbo].[sqlwatch_meta_check] ADD  CONSTRAINT [df_sqlwatch_meta_check_updated]  DEFAULT (getutcdate()) FOR [date_updated]
GO
ALTER TABLE [dbo].[sqlwatch_meta_check] ADD  DEFAULT ((1)) FOR [check_enabled]
GO
ALTER TABLE [dbo].[sqlwatch_meta_check] ADD  DEFAULT ((1)) FOR [use_baseline]
GO
ALTER TABLE [dbo].[sqlwatch_meta_database] ADD  CONSTRAINT [df_sqlwatch_meta_database_db_create_data]  DEFAULT ('1970-01-01') FOR [database_create_date]
GO
ALTER TABLE [dbo].[sqlwatch_meta_database] ADD  CONSTRAINT [df_sqlwatch_meta_database_sql_instance]  DEFAULT (@@servername) FOR [sql_instance]
GO
ALTER TABLE [dbo].[sqlwatch_meta_database] ADD  CONSTRAINT [df_sqlwatch_meta_database_last_seen]  DEFAULT (getutcdate()) FOR [date_last_seen]
GO
ALTER TABLE [dbo].[sqlwatch_meta_errorlog_attribute] ADD  DEFAULT (@@servername) FOR [sql_instance]
GO
ALTER TABLE [dbo].[sqlwatch_meta_errorlog_attribute] ADD  CONSTRAINT [df_sqlwatch_meta_errorlog_attribute_updated]  DEFAULT (getutcdate()) FOR [date_updated]
GO
ALTER TABLE [dbo].[sqlwatch_meta_errorlog_keyword] ADD  CONSTRAINT [df_sqlwatch_meta_errorlog_keyword_updated]  DEFAULT (getutcdate()) FOR [date_updated]
GO
ALTER TABLE [dbo].[sqlwatch_meta_errorlog_text] ADD  DEFAULT (@@servername) FOR [sql_instance]
GO
ALTER TABLE [dbo].[sqlwatch_meta_errorlog_text] ADD  CONSTRAINT [df_sqlwatch_meta_errorlog_text_updated]  DEFAULT (getutcdate()) FOR [date_updated]
GO
ALTER TABLE [dbo].[sqlwatch_meta_index] ADD  CONSTRAINT [df_sqlwatch_meta_index_date_created]  DEFAULT (getutcdate()) FOR [date_created]
GO
ALTER TABLE [dbo].[sqlwatch_meta_index] ADD  CONSTRAINT [df_sqlwatch_meta_index_last_seen]  DEFAULT (getutcdate()) FOR [date_last_seen]
GO
ALTER TABLE [dbo].[sqlwatch_meta_index_missing] ADD  CONSTRAINT [df_sqlwatch_meta_index_missing_sql_instance]  DEFAULT (@@servername) FOR [sql_instance]
GO
ALTER TABLE [dbo].[sqlwatch_meta_index_missing] ADD  CONSTRAINT [df_sqlwatch_meta_index_missing_date_created]  DEFAULT (getutcdate()) FOR [date_created]
GO
ALTER TABLE [dbo].[sqlwatch_meta_index_missing] ADD  CONSTRAINT [df_sqlwatch_meta_index_missing_last_seen]  DEFAULT (getutcdate()) FOR [date_last_seen]
GO
ALTER TABLE [dbo].[sqlwatch_meta_master_file] ADD  CONSTRAINT [df_sqlwatch_meta_master_file_sql_instance]  DEFAULT (@@servername) FOR [sql_instance]
GO
ALTER TABLE [dbo].[sqlwatch_meta_master_file] ADD  CONSTRAINT [df_sqlwatch_meta_master_file_last_seen]  DEFAULT (getutcdate()) FOR [date_last_seen]
GO
ALTER TABLE [dbo].[sqlwatch_meta_memory_clerk] ADD  CONSTRAINT [df_sqlwatch_meta_memory_clerk_sql_instance]  DEFAULT (@@servername) FOR [sql_instance]
GO
ALTER TABLE [dbo].[sqlwatch_meta_memory_clerk] ADD  CONSTRAINT [df_sqlwatch_meta_memory_clerk_updated]  DEFAULT (getutcdate()) FOR [date_updated]
GO
ALTER TABLE [dbo].[sqlwatch_meta_os_volume] ADD  CONSTRAINT [df_sqlwatch_meta_os_volume_sql_instance]  DEFAULT (@@servername) FOR [sql_instance]
GO
ALTER TABLE [dbo].[sqlwatch_meta_os_volume] ADD  CONSTRAINT [df_sqlwatch_meta_os_volume_date_created]  DEFAULT (getutcdate()) FOR [date_created]
GO
ALTER TABLE [dbo].[sqlwatch_meta_performance_counter] ADD  CONSTRAINT [df_sqlwatch_meta_performance_counter_updated]  DEFAULT (getutcdate()) FOR [date_updated]
GO
ALTER TABLE [dbo].[sqlwatch_meta_report] ADD  CONSTRAINT [df_sqlwatch_meta_report_updated]  DEFAULT (getutcdate()) FOR [date_updated]
GO
ALTER TABLE [dbo].[sqlwatch_meta_server] ADD  CONSTRAINT [df_sqlwatch_meta_server_offset]  DEFAULT (datediff(minute,getutcdate(),getdate())) FOR [utc_offset_minutes]
GO
ALTER TABLE [dbo].[sqlwatch_meta_server] ADD  CONSTRAINT [df_sqlwatch_meta_server_updated]  DEFAULT (getutcdate()) FOR [date_updated]
GO
ALTER TABLE [dbo].[sqlwatch_meta_system_configuration] ADD  CONSTRAINT [df_sqlwatch_meta_system_configuration_sql_instance]  DEFAULT (@@servername) FOR [sql_instance]
GO
ALTER TABLE [dbo].[sqlwatch_meta_system_configuration] ADD  CONSTRAINT [df_sqlwatch_meta_system_configuration_date_created]  DEFAULT (getutcdate()) FOR [date_created]
GO
ALTER TABLE [dbo].[sqlwatch_meta_system_configuration] ADD  CONSTRAINT [df_sqlwatch_meta_system_configuration_updated]  DEFAULT (getutcdate()) FOR [date_updated]
GO
ALTER TABLE [dbo].[sqlwatch_meta_system_configuration_scd] ADD  DEFAULT (@@servername) FOR [sql_instance]
GO
ALTER TABLE [dbo].[sqlwatch_meta_system_configuration_scd] ADD  CONSTRAINT [sqlwatch_logger_system_configuration_scd_valid_from]  DEFAULT (getutcdate()) FOR [valid_from]
GO
ALTER TABLE [dbo].[sqlwatch_meta_system_configuration_scd] ADD  CONSTRAINT [df_sqlwatch_meta_system_configuration_scd_updated]  DEFAULT (getutcdate()) FOR [date_updated]
GO
ALTER TABLE [dbo].[sqlwatch_meta_table] ADD  CONSTRAINT [df_sqlwatch_meta_table_date_created]  DEFAULT (getutcdate()) FOR [date_first_seen]
GO
ALTER TABLE [dbo].[sqlwatch_meta_table] ADD  CONSTRAINT [df_sqlwatch_meta_table_last_seen]  DEFAULT (getutcdate()) FOR [date_last_seen]
GO
ALTER TABLE [dbo].[sqlwatch_meta_wait_stats] ADD  CONSTRAINT [df_sqlwatch_meta_wait_stats_updated]  DEFAULT (getutcdate()) FOR [date_updated]
GO
ALTER TABLE [dbo].[sqlwatch_app_log]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_sys_log] FOREIGN KEY([sql_instance])
REFERENCES [dbo].[sqlwatch_meta_server] ([servername])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_app_log] CHECK CONSTRAINT [fk_sqlwatch_sys_log]
GO
ALTER TABLE [dbo].[sqlwatch_config_action]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_config_action_report] FOREIGN KEY([action_report_id])
REFERENCES [dbo].[sqlwatch_config_report] ([report_id])
GO
ALTER TABLE [dbo].[sqlwatch_config_action] CHECK CONSTRAINT [fk_sqlwatch_config_action_report]
GO
ALTER TABLE [dbo].[sqlwatch_config_check_action]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_config_check_action_action] FOREIGN KEY([action_id])
REFERENCES [dbo].[sqlwatch_config_action] ([action_id])
GO
ALTER TABLE [dbo].[sqlwatch_config_check_action] CHECK CONSTRAINT [fk_sqlwatch_config_check_action_action]
GO
ALTER TABLE [dbo].[sqlwatch_config_check_action]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_config_check_action_check] FOREIGN KEY([check_id])
REFERENCES [dbo].[sqlwatch_config_check] ([check_id])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_config_check_action] CHECK CONSTRAINT [fk_sqlwatch_config_check_action_check]
GO
ALTER TABLE [dbo].[sqlwatch_config_check_action]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_config_check_action_template] FOREIGN KEY([action_template_id])
REFERENCES [dbo].[sqlwatch_config_check_action_template] ([action_template_id])
GO
ALTER TABLE [dbo].[sqlwatch_config_check_action] CHECK CONSTRAINT [fk_sqlwatch_config_check_action_template]
GO
ALTER TABLE [dbo].[sqlwatch_config_check_template_action]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_config_check_template_action_check_name] FOREIGN KEY([check_name])
REFERENCES [dbo].[sqlwatch_config_check_template] ([check_name])
GO
ALTER TABLE [dbo].[sqlwatch_config_check_template_action] CHECK CONSTRAINT [fk_sqlwatch_config_check_template_action_check_name]
GO
ALTER TABLE [dbo].[sqlwatch_config_exclude_database]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_config_logger_exclude_database_snapshot_type] FOREIGN KEY([snapshot_type_id])
REFERENCES [dbo].[sqlwatch_config_snapshot_type] ([snapshot_type_id])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_config_exclude_database] CHECK CONSTRAINT [fk_sqlwatch_config_logger_exclude_database_snapshot_type]
GO
ALTER TABLE [dbo].[sqlwatch_config_exclude_procedure]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_config_exclude_procedure_snapshot_type] FOREIGN KEY([snapshot_type_id])
REFERENCES [dbo].[sqlwatch_config_snapshot_type] ([snapshot_type_id])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_config_exclude_procedure] CHECK CONSTRAINT [fk_sqlwatch_config_exclude_procedure_snapshot_type]
GO
ALTER TABLE [dbo].[sqlwatch_config_report]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_config_report_style] FOREIGN KEY([report_style_id])
REFERENCES [dbo].[sqlwatch_config_report_style] ([report_style_id])
GO
ALTER TABLE [dbo].[sqlwatch_config_report] CHECK CONSTRAINT [fk_sqlwatch_config_report_style]
GO
ALTER TABLE [dbo].[sqlwatch_config_report_action]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_config_report_action_action] FOREIGN KEY([action_id])
REFERENCES [dbo].[sqlwatch_config_action] ([action_id])
GO
ALTER TABLE [dbo].[sqlwatch_config_report_action] CHECK CONSTRAINT [fk_sqlwatch_config_report_action_action]
GO
ALTER TABLE [dbo].[sqlwatch_config_report_action]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_config_report_action_report] FOREIGN KEY([report_id])
REFERENCES [dbo].[sqlwatch_config_report] ([report_id])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_config_report_action] CHECK CONSTRAINT [fk_sqlwatch_config_report_action_report]
GO
ALTER TABLE [dbo].[sqlwatch_logger_agent_job_history]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_logger_agent_job_history_job] FOREIGN KEY([sql_instance], [sqlwatch_job_id], [sqlwatch_job_step_id])
REFERENCES [dbo].[sqlwatch_meta_agent_job_step] ([sql_instance], [sqlwatch_job_id], [sqlwatch_job_step_id])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_logger_agent_job_history] CHECK CONSTRAINT [fk_sqlwatch_logger_agent_job_history_job]
GO
ALTER TABLE [dbo].[sqlwatch_logger_agent_job_history]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_logger_agent_job_history_snapshot_header] FOREIGN KEY([snapshot_time], [sql_instance], [snapshot_type_id])
REFERENCES [dbo].[sqlwatch_logger_snapshot_header] ([snapshot_time], [sql_instance], [snapshot_type_id])
ON UPDATE CASCADE
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_logger_agent_job_history] CHECK CONSTRAINT [fk_sqlwatch_logger_agent_job_history_snapshot_header]
GO
ALTER TABLE [dbo].[sqlwatch_logger_check]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_logger_check_header] FOREIGN KEY([snapshot_time], [sql_instance], [snapshot_type_id])
REFERENCES [dbo].[sqlwatch_logger_snapshot_header] ([snapshot_time], [sql_instance], [snapshot_type_id])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_logger_check] CHECK CONSTRAINT [fk_sqlwatch_logger_check_header]
GO
ALTER TABLE [dbo].[sqlwatch_logger_check]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_meta_check] FOREIGN KEY([sql_instance], [check_id])
REFERENCES [dbo].[sqlwatch_meta_check] ([sql_instance], [check_id])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_logger_check] CHECK CONSTRAINT [fk_sqlwatch_meta_check]
GO
ALTER TABLE [dbo].[sqlwatch_logger_check_action]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_logger_check_action_header] FOREIGN KEY([snapshot_time], [sql_instance], [snapshot_type_id])
REFERENCES [dbo].[sqlwatch_logger_snapshot_header] ([snapshot_time], [sql_instance], [snapshot_type_id])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_logger_check_action] CHECK CONSTRAINT [fk_sqlwatch_logger_check_action_header]
GO
ALTER TABLE [dbo].[sqlwatch_logger_disk_utilisation_database]  WITH CHECK ADD  CONSTRAINT [FK_logger_disk_util_database_database] FOREIGN KEY([sql_instance], [sqlwatch_database_id])
REFERENCES [dbo].[sqlwatch_meta_database] ([sql_instance], [sqlwatch_database_id])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_logger_disk_utilisation_database] CHECK CONSTRAINT [FK_logger_disk_util_database_database]
GO
ALTER TABLE [dbo].[sqlwatch_logger_disk_utilisation_database]  WITH CHECK ADD  CONSTRAINT [FK_logger_disk_util_database_snapshot] FOREIGN KEY([snapshot_time], [sql_instance], [snapshot_type_id])
REFERENCES [dbo].[sqlwatch_logger_snapshot_header] ([snapshot_time], [sql_instance], [snapshot_type_id])
ON UPDATE CASCADE
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_logger_disk_utilisation_database] CHECK CONSTRAINT [FK_logger_disk_util_database_snapshot]
GO
ALTER TABLE [dbo].[sqlwatch_logger_disk_utilisation_table]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_logger_disk_utilisation_table_header] FOREIGN KEY([snapshot_time], [sql_instance], [snapshot_type_id])
REFERENCES [dbo].[sqlwatch_logger_snapshot_header] ([snapshot_time], [sql_instance], [snapshot_type_id])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_logger_disk_utilisation_table] CHECK CONSTRAINT [fk_sqlwatch_logger_disk_utilisation_table_header]
GO
ALTER TABLE [dbo].[sqlwatch_logger_disk_utilisation_table]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_logger_disk_utilistation_table_meta_table] FOREIGN KEY([sql_instance], [sqlwatch_database_id], [sqlwatch_table_id])
REFERENCES [dbo].[sqlwatch_meta_table] ([sql_instance], [sqlwatch_database_id], [sqlwatch_table_id])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_logger_disk_utilisation_table] CHECK CONSTRAINT [fk_sqlwatch_logger_disk_utilistation_table_meta_table]
GO
ALTER TABLE [dbo].[sqlwatch_logger_disk_utilisation_volume]  WITH CHECK ADD  CONSTRAINT [FK_disk_util_vol_snapshot_header] FOREIGN KEY([snapshot_time], [sql_instance], [snapshot_type_id])
REFERENCES [dbo].[sqlwatch_logger_snapshot_header] ([snapshot_time], [sql_instance], [snapshot_type_id])
ON UPDATE CASCADE
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_logger_disk_utilisation_volume] CHECK CONSTRAINT [FK_disk_util_vol_snapshot_header]
GO
ALTER TABLE [dbo].[sqlwatch_logger_disk_utilisation_volume]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_logger_disk_utilisation_volume_id] FOREIGN KEY([sql_instance], [sqlwatch_volume_id])
REFERENCES [dbo].[sqlwatch_meta_os_volume] ([sql_instance], [sqlwatch_volume_id])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_logger_disk_utilisation_volume] CHECK CONSTRAINT [fk_sqlwatch_logger_disk_utilisation_volume_id]
GO
ALTER TABLE [dbo].[sqlwatch_logger_dm_exec_requests_stats]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_logger_dm_exec_requests_snapshot_header] FOREIGN KEY([snapshot_time], [sql_instance], [snapshot_type_id])
REFERENCES [dbo].[sqlwatch_logger_snapshot_header] ([snapshot_time], [sql_instance], [snapshot_type_id])
ON UPDATE CASCADE
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_logger_dm_exec_requests_stats] CHECK CONSTRAINT [fk_sqlwatch_logger_dm_exec_requests_snapshot_header]
GO
ALTER TABLE [dbo].[sqlwatch_logger_dm_exec_sessions_stats]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_logger_dm_exec_sessions_snapshot_header] FOREIGN KEY([snapshot_time], [sql_instance], [snapshot_type_id])
REFERENCES [dbo].[sqlwatch_logger_snapshot_header] ([snapshot_time], [sql_instance], [snapshot_type_id])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_logger_dm_exec_sessions_stats] CHECK CONSTRAINT [fk_sqlwatch_logger_dm_exec_sessions_snapshot_header]
GO
ALTER TABLE [dbo].[sqlwatch_logger_errorlog]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_logger_errorlog_keyword] FOREIGN KEY([sql_instance], [keyword_id], [log_type_id])
REFERENCES [dbo].[sqlwatch_meta_errorlog_keyword] ([sql_instance], [keyword_id], [log_type_id])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_logger_errorlog] CHECK CONSTRAINT [fk_sqlwatch_logger_errorlog_keyword]
GO
ALTER TABLE [dbo].[sqlwatch_logger_errorlog]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_logger_errorlog_snapshot] FOREIGN KEY([snapshot_time], [sql_instance], [snapshot_type_id])
REFERENCES [dbo].[sqlwatch_logger_snapshot_header] ([snapshot_time], [sql_instance], [snapshot_type_id])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_logger_errorlog] CHECK CONSTRAINT [fk_sqlwatch_logger_errorlog_snapshot]
GO
ALTER TABLE [dbo].[sqlwatch_logger_hadr_database_replica_states]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_logger_hadr_database_replica_states_header] FOREIGN KEY([snapshot_time], [sql_instance], [snapshot_type_id])
REFERENCES [dbo].[sqlwatch_logger_snapshot_header] ([snapshot_time], [sql_instance], [snapshot_type_id])
ON UPDATE CASCADE
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_logger_hadr_database_replica_states] CHECK CONSTRAINT [fk_sqlwatch_logger_hadr_database_replica_states_header]
GO
ALTER TABLE [dbo].[sqlwatch_logger_index_histogram]  WITH CHECK ADD  CONSTRAINT [fk_logger_index_histogram] FOREIGN KEY([snapshot_time], [sql_instance], [snapshot_type_id])
REFERENCES [dbo].[sqlwatch_logger_snapshot_header] ([snapshot_time], [sql_instance], [snapshot_type_id])
ON UPDATE CASCADE
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_logger_index_histogram] CHECK CONSTRAINT [fk_logger_index_histogram]
GO
ALTER TABLE [dbo].[sqlwatch_logger_index_histogram]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_logger_index_histogram_index] FOREIGN KEY([sql_instance], [sqlwatch_database_id], [sqlwatch_table_id], [sqlwatch_index_id])
REFERENCES [dbo].[sqlwatch_meta_index] ([sql_instance], [sqlwatch_database_id], [sqlwatch_table_id], [sqlwatch_index_id])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_logger_index_histogram] CHECK CONSTRAINT [fk_sqlwatch_logger_index_histogram_index]
GO
ALTER TABLE [dbo].[sqlwatch_logger_index_missing_stats]  WITH CHECK ADD  CONSTRAINT [fk_logger_missing_indexes_snapshot_header] FOREIGN KEY([snapshot_time], [sql_instance], [snapshot_type_id])
REFERENCES [dbo].[sqlwatch_logger_snapshot_header] ([snapshot_time], [sql_instance], [snapshot_type_id])
ON UPDATE CASCADE
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_logger_index_missing_stats] CHECK CONSTRAINT [fk_logger_missing_indexes_snapshot_header]
GO
ALTER TABLE [dbo].[sqlwatch_logger_index_missing_stats]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_logger_index_missing_stats_index_detail] FOREIGN KEY([sql_instance], [sqlwatch_database_id], [sqlwatch_table_id], [sqlwatch_missing_index_id])
REFERENCES [dbo].[sqlwatch_meta_index_missing] ([sql_instance], [sqlwatch_database_id], [sqlwatch_table_id], [sqlwatch_missing_index_id])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_logger_index_missing_stats] CHECK CONSTRAINT [fk_sqlwatch_logger_index_missing_stats_index_detail]
GO
ALTER TABLE [dbo].[sqlwatch_logger_index_usage_stats]  WITH CHECK ADD  CONSTRAINT [fk_index_usage_stats_header] FOREIGN KEY([snapshot_time], [sql_instance], [snapshot_type_id])
REFERENCES [dbo].[sqlwatch_logger_snapshot_header] ([snapshot_time], [sql_instance], [snapshot_type_id])
ON UPDATE CASCADE
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_logger_index_usage_stats] CHECK CONSTRAINT [fk_index_usage_stats_header]
GO
ALTER TABLE [dbo].[sqlwatch_logger_index_usage_stats]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_logger_index_usage_stats_index] FOREIGN KEY([sql_instance], [sqlwatch_database_id], [sqlwatch_table_id], [sqlwatch_index_id])
REFERENCES [dbo].[sqlwatch_meta_index] ([sql_instance], [sqlwatch_database_id], [sqlwatch_table_id], [sqlwatch_index_id])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_logger_index_usage_stats] CHECK CONSTRAINT [fk_sqlwatch_logger_index_usage_stats_index]
GO
ALTER TABLE [dbo].[sqlwatch_logger_perf_file_stats]  WITH CHECK ADD  CONSTRAINT [fk_sql_perf_mon_file_stats_snapshot_header] FOREIGN KEY([snapshot_time], [sql_instance], [snapshot_type_id])
REFERENCES [dbo].[sqlwatch_logger_snapshot_header] ([snapshot_time], [sql_instance], [snapshot_type_id])
ON UPDATE CASCADE
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_logger_perf_file_stats] CHECK CONSTRAINT [fk_sql_perf_mon_file_stats_snapshot_header]
GO
ALTER TABLE [dbo].[sqlwatch_logger_perf_file_stats]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_logger_perf_file_stats_master_file] FOREIGN KEY([sql_instance], [sqlwatch_database_id], [sqlwatch_master_file_id])
REFERENCES [dbo].[sqlwatch_meta_master_file] ([sql_instance], [sqlwatch_database_id], [sqlwatch_master_file_id])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_logger_perf_file_stats] CHECK CONSTRAINT [fk_sqlwatch_logger_perf_file_stats_master_file]
GO
ALTER TABLE [dbo].[sqlwatch_logger_perf_os_memory_clerks]  WITH CHECK ADD  CONSTRAINT [fk_sql_perf_mon_os_memory_clerks_snapshot_header] FOREIGN KEY([snapshot_time], [sql_instance], [snapshot_type_id])
REFERENCES [dbo].[sqlwatch_logger_snapshot_header] ([snapshot_time], [sql_instance], [snapshot_type_id])
ON UPDATE CASCADE
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_logger_perf_os_memory_clerks] CHECK CONSTRAINT [fk_sql_perf_mon_os_memory_clerks_snapshot_header]
GO
ALTER TABLE [dbo].[sqlwatch_logger_perf_os_memory_clerks]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_logger_perf_os_memory_clerks_meta] FOREIGN KEY([sql_instance], [sqlwatch_mem_clerk_id])
REFERENCES [dbo].[sqlwatch_meta_memory_clerk] ([sql_instance], [sqlwatch_mem_clerk_id])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_logger_perf_os_memory_clerks] CHECK CONSTRAINT [fk_sqlwatch_logger_perf_os_memory_clerks_meta]
GO
ALTER TABLE [dbo].[sqlwatch_logger_perf_os_performance_counters]  WITH CHECK ADD  CONSTRAINT [fk_sql_perf_mon_perf_counters_snapshot_header] FOREIGN KEY([snapshot_time], [sql_instance], [snapshot_type_id])
REFERENCES [dbo].[sqlwatch_logger_snapshot_header] ([snapshot_time], [sql_instance], [snapshot_type_id])
ON UPDATE CASCADE
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_logger_perf_os_performance_counters] CHECK CONSTRAINT [fk_sql_perf_mon_perf_counters_snapshot_header]
GO
ALTER TABLE [dbo].[sqlwatch_logger_perf_os_performance_counters]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_logger_perf_os_performance_counters_meta] FOREIGN KEY([sql_instance], [performance_counter_id])
REFERENCES [dbo].[sqlwatch_meta_performance_counter] ([sql_instance], [performance_counter_id])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_logger_perf_os_performance_counters] CHECK CONSTRAINT [fk_sqlwatch_logger_perf_os_performance_counters_meta]
GO
ALTER TABLE [dbo].[sqlwatch_logger_perf_os_process_memory]  WITH CHECK ADD  CONSTRAINT [fk_sql_perf_mon_os_process_memory_snapshot_header] FOREIGN KEY([snapshot_time], [sql_instance], [snapshot_type_id])
REFERENCES [dbo].[sqlwatch_logger_snapshot_header] ([snapshot_time], [sql_instance], [snapshot_type_id])
ON UPDATE CASCADE
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_logger_perf_os_process_memory] CHECK CONSTRAINT [fk_sql_perf_mon_os_process_memory_snapshot_header]
GO
ALTER TABLE [dbo].[sqlwatch_logger_perf_os_process_memory]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_logger_perf_os_process_memory_server] FOREIGN KEY([sql_instance])
REFERENCES [dbo].[sqlwatch_meta_server] ([servername])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_logger_perf_os_process_memory] CHECK CONSTRAINT [fk_sqlwatch_logger_perf_os_process_memory_server]
GO
ALTER TABLE [dbo].[sqlwatch_logger_perf_os_schedulers]  WITH CHECK ADD  CONSTRAINT [fk_logger_perf_os_schedulers] FOREIGN KEY([snapshot_time], [sql_instance], [snapshot_type_id])
REFERENCES [dbo].[sqlwatch_logger_snapshot_header] ([snapshot_time], [sql_instance], [snapshot_type_id])
ON UPDATE CASCADE
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_logger_perf_os_schedulers] CHECK CONSTRAINT [fk_logger_perf_os_schedulers]
GO
ALTER TABLE [dbo].[sqlwatch_logger_perf_os_schedulers]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_logger_perf_os_schedulers_server] FOREIGN KEY([sql_instance])
REFERENCES [dbo].[sqlwatch_meta_server] ([servername])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_logger_perf_os_schedulers] CHECK CONSTRAINT [fk_sqlwatch_logger_perf_os_schedulers_server]
GO
ALTER TABLE [dbo].[sqlwatch_logger_perf_os_wait_stats]  WITH CHECK ADD  CONSTRAINT [fk_sql_perf_mon_wait_stats_snapshot_header] FOREIGN KEY([snapshot_time], [sql_instance], [snapshot_type_id])
REFERENCES [dbo].[sqlwatch_logger_snapshot_header] ([snapshot_time], [sql_instance], [snapshot_type_id])
ON UPDATE CASCADE
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_logger_perf_os_wait_stats] CHECK CONSTRAINT [fk_sql_perf_mon_wait_stats_snapshot_header]
GO
ALTER TABLE [dbo].[sqlwatch_logger_perf_os_wait_stats]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_logger_perf_os_wait_stats_wait_type_id] FOREIGN KEY([sql_instance], [wait_type_id])
REFERENCES [dbo].[sqlwatch_meta_wait_stats] ([sql_instance], [wait_type_id])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_logger_perf_os_wait_stats] CHECK CONSTRAINT [fk_sqlwatch_logger_perf_os_wait_stats_wait_type_id]
GO
ALTER TABLE [dbo].[sqlwatch_logger_perf_procedure_stats]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_logger_perf_procedure_stats_procedure] FOREIGN KEY([sql_instance], [sqlwatch_database_id], [sqlwatch_procedure_id])
REFERENCES [dbo].[sqlwatch_meta_procedure] ([sql_instance], [sqlwatch_database_id], [sqlwatch_procedure_id])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_logger_perf_procedure_stats] CHECK CONSTRAINT [fk_sqlwatch_logger_perf_procedure_stats_procedure]
GO
ALTER TABLE [dbo].[sqlwatch_logger_perf_procedure_stats]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_logger_perf_procedure_stats_snapshot_header] FOREIGN KEY([snapshot_time], [sql_instance], [snapshot_type_id])
REFERENCES [dbo].[sqlwatch_logger_snapshot_header] ([snapshot_time], [sql_instance], [snapshot_type_id])
ON UPDATE CASCADE
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_logger_perf_procedure_stats] CHECK CONSTRAINT [fk_sqlwatch_logger_perf_procedure_stats_snapshot_header]
GO
ALTER TABLE [dbo].[sqlwatch_logger_perf_query_stats]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_logger_perf_query_stats_snapshot_header] FOREIGN KEY([snapshot_time], [sql_instance], [snapshot_type_id])
REFERENCES [dbo].[sqlwatch_logger_snapshot_header] ([snapshot_time], [sql_instance], [snapshot_type_id])
ON UPDATE CASCADE
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_logger_perf_query_stats] CHECK CONSTRAINT [fk_sqlwatch_logger_perf_query_stats_snapshot_header]
GO
ALTER TABLE [dbo].[sqlwatch_logger_report_action]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_logger_report_action_header] FOREIGN KEY([snapshot_time], [sql_instance], [snapshot_type_id])
REFERENCES [dbo].[sqlwatch_logger_snapshot_header] ([snapshot_time], [sql_instance], [snapshot_type_id])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_logger_report_action] CHECK CONSTRAINT [fk_sqlwatch_logger_report_action_header]
GO
ALTER TABLE [dbo].[sqlwatch_logger_snapshot_header]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_logger_snapshot_header_type_id] FOREIGN KEY([snapshot_type_id])
REFERENCES [dbo].[sqlwatch_config_snapshot_type] ([snapshot_type_id])
GO
ALTER TABLE [dbo].[sqlwatch_logger_snapshot_header] CHECK CONSTRAINT [fk_sqlwatch_logger_snapshot_header_type_id]
GO
ALTER TABLE [dbo].[sqlwatch_logger_system_configuration]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_logger_system_configuration_keyword] FOREIGN KEY([sql_instance], [sqlwatch_configuration_id])
REFERENCES [dbo].[sqlwatch_meta_system_configuration] ([sql_instance], [sqlwatch_configuration_id])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_logger_system_configuration] CHECK CONSTRAINT [fk_sqlwatch_logger_system_configuration_keyword]
GO
ALTER TABLE [dbo].[sqlwatch_logger_system_configuration]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_logger_system_configuration_snapshot] FOREIGN KEY([snapshot_time], [sql_instance], [snapshot_type_id])
REFERENCES [dbo].[sqlwatch_logger_snapshot_header] ([snapshot_time], [sql_instance], [snapshot_type_id])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_logger_system_configuration] CHECK CONSTRAINT [fk_sqlwatch_logger_system_configuration_snapshot]
GO
ALTER TABLE [dbo].[sqlwatch_logger_whoisactive]  WITH CHECK ADD  CONSTRAINT [fk_sql_perf_mon_who_is_active_snapshot_header] FOREIGN KEY([snapshot_time], [sql_instance], [snapshot_type_id])
REFERENCES [dbo].[sqlwatch_logger_snapshot_header] ([snapshot_time], [sql_instance], [snapshot_type_id])
ON UPDATE CASCADE
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_logger_whoisactive] CHECK CONSTRAINT [fk_sql_perf_mon_who_is_active_snapshot_header]
GO
ALTER TABLE [dbo].[sqlwatch_logger_whoisactive]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_logger_whoisactive_server] FOREIGN KEY([sql_instance])
REFERENCES [dbo].[sqlwatch_meta_server] ([servername])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_logger_whoisactive] CHECK CONSTRAINT [fk_sqlwatch_logger_whoisactive_server]
GO
ALTER TABLE [dbo].[sqlwatch_logger_xes_blockers]  WITH CHECK ADD  CONSTRAINT [fk_logger_perf_xes_blockers] FOREIGN KEY([snapshot_time], [sql_instance], [snapshot_type_id])
REFERENCES [dbo].[sqlwatch_logger_snapshot_header] ([snapshot_time], [sql_instance], [snapshot_type_id])
ON UPDATE CASCADE
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_logger_xes_blockers] CHECK CONSTRAINT [fk_logger_perf_xes_blockers]
GO
ALTER TABLE [dbo].[sqlwatch_logger_xes_blockers]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_logger_xes_blockers_server] FOREIGN KEY([sql_instance])
REFERENCES [dbo].[sqlwatch_meta_server] ([servername])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_logger_xes_blockers] CHECK CONSTRAINT [fk_sqlwatch_logger_xes_blockers_server]
GO
ALTER TABLE [dbo].[sqlwatch_logger_xes_iosubsystem]  WITH CHECK ADD  CONSTRAINT [fk_logger_performance_xes_iosubsystem_snapshot_header] FOREIGN KEY([snapshot_time], [sql_instance], [snapshot_type_id])
REFERENCES [dbo].[sqlwatch_logger_snapshot_header] ([snapshot_time], [sql_instance], [snapshot_type_id])
ON UPDATE CASCADE
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_logger_xes_iosubsystem] CHECK CONSTRAINT [fk_logger_performance_xes_iosubsystem_snapshot_header]
GO
ALTER TABLE [dbo].[sqlwatch_logger_xes_iosubsystem]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_logger_xes_iosubsystem_server] FOREIGN KEY([sql_instance])
REFERENCES [dbo].[sqlwatch_meta_server] ([servername])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_logger_xes_iosubsystem] CHECK CONSTRAINT [fk_sqlwatch_logger_xes_iosubsystem_server]
GO
ALTER TABLE [dbo].[sqlwatch_logger_xes_long_queries]  WITH CHECK ADD  CONSTRAINT [fk_logger_perf_xes_long_queries] FOREIGN KEY([snapshot_time], [sql_instance], [snapshot_type_id])
REFERENCES [dbo].[sqlwatch_logger_snapshot_header] ([snapshot_time], [sql_instance], [snapshot_type_id])
ON UPDATE CASCADE
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_logger_xes_long_queries] CHECK CONSTRAINT [fk_logger_perf_xes_long_queries]
GO
ALTER TABLE [dbo].[sqlwatch_logger_xes_long_queries]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_logger_xes_long_queries_database] FOREIGN KEY([sql_instance], [sqlwatch_database_id])
REFERENCES [dbo].[sqlwatch_meta_database] ([sql_instance], [sqlwatch_database_id])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_logger_xes_long_queries] CHECK CONSTRAINT [fk_sqlwatch_logger_xes_long_queries_database]
GO
ALTER TABLE [dbo].[sqlwatch_logger_xes_query_problems]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_logger_xes_query_problems_header] FOREIGN KEY([snapshot_time], [sql_instance], [snapshot_type_id])
REFERENCES [dbo].[sqlwatch_logger_snapshot_header] ([snapshot_time], [sql_instance], [snapshot_type_id])
ON UPDATE CASCADE
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_logger_xes_query_problems] CHECK CONSTRAINT [fk_sqlwatch_logger_xes_query_problems_header]
GO
ALTER TABLE [dbo].[sqlwatch_logger_xes_query_problems]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_logger_xes_query_problems_servername] FOREIGN KEY([sql_instance])
REFERENCES [dbo].[sqlwatch_meta_server] ([servername])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_logger_xes_query_problems] CHECK CONSTRAINT [fk_sqlwatch_logger_xes_query_problems_servername]
GO
ALTER TABLE [dbo].[sqlwatch_logger_xes_query_processing]  WITH CHECK ADD  CONSTRAINT [fk_logger_xe_query_processing_snapshot_header] FOREIGN KEY([snapshot_time], [sql_instance], [snapshot_type_id])
REFERENCES [dbo].[sqlwatch_logger_snapshot_header] ([snapshot_time], [sql_instance], [snapshot_type_id])
ON UPDATE CASCADE
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_logger_xes_query_processing] CHECK CONSTRAINT [fk_logger_xe_query_processing_snapshot_header]
GO
ALTER TABLE [dbo].[sqlwatch_logger_xes_query_processing]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_logger_xes_query_processing_server] FOREIGN KEY([sql_instance])
REFERENCES [dbo].[sqlwatch_meta_server] ([servername])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_logger_xes_query_processing] CHECK CONSTRAINT [fk_sqlwatch_logger_xes_query_processing_server]
GO
ALTER TABLE [dbo].[sqlwatch_logger_xes_wait_event]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_logger_xes_wait_stat_event_server] FOREIGN KEY([sql_instance])
REFERENCES [dbo].[sqlwatch_meta_server] ([servername])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_logger_xes_wait_event] CHECK CONSTRAINT [fk_sqlwatch_logger_xes_wait_stat_event_server]
GO
ALTER TABLE [dbo].[sqlwatch_logger_xes_wait_event]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_logger_xes_wait_stat_event_snapshot_header] FOREIGN KEY([snapshot_time], [sql_instance], [snapshot_type_id])
REFERENCES [dbo].[sqlwatch_logger_snapshot_header] ([snapshot_time], [sql_instance], [snapshot_type_id])
ON UPDATE CASCADE
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_logger_xes_wait_event] CHECK CONSTRAINT [fk_sqlwatch_logger_xes_wait_stat_event_snapshot_header]
GO
ALTER TABLE [dbo].[sqlwatch_meta_agent_job]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_meta_agent_job_server] FOREIGN KEY([sql_instance])
REFERENCES [dbo].[sqlwatch_meta_server] ([servername])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_meta_agent_job] CHECK CONSTRAINT [fk_sqlwatch_meta_agent_job_server]
GO
ALTER TABLE [dbo].[sqlwatch_meta_agent_job_step]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_meta_agent_job_id] FOREIGN KEY([sql_instance], [sqlwatch_job_id])
REFERENCES [dbo].[sqlwatch_meta_agent_job] ([sql_instance], [sqlwatch_job_id])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_meta_agent_job_step] CHECK CONSTRAINT [fk_sqlwatch_meta_agent_job_id]
GO
ALTER TABLE [dbo].[sqlwatch_meta_baseline]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_meta_retention_sql_instance] FOREIGN KEY([sql_instance])
REFERENCES [dbo].[sqlwatch_meta_server] ([servername])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_meta_baseline] CHECK CONSTRAINT [fk_sqlwatch_meta_retention_sql_instance]
GO
ALTER TABLE [dbo].[sqlwatch_meta_check]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_meta_check_server] FOREIGN KEY([sql_instance])
REFERENCES [dbo].[sqlwatch_meta_server] ([servername])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_meta_check] CHECK CONSTRAINT [fk_sqlwatch_meta_check_server]
GO
ALTER TABLE [dbo].[sqlwatch_meta_database]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_meta_database_server] FOREIGN KEY([sql_instance])
REFERENCES [dbo].[sqlwatch_meta_server] ([servername])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_meta_database] CHECK CONSTRAINT [fk_sqlwatch_meta_database_server]
GO
ALTER TABLE [dbo].[sqlwatch_meta_errorlog_attribute]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_meta_errorlog_attributes_server] FOREIGN KEY([sql_instance])
REFERENCES [dbo].[sqlwatch_meta_server] ([servername])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_meta_errorlog_attribute] CHECK CONSTRAINT [fk_sqlwatch_meta_errorlog_attributes_server]
GO
ALTER TABLE [dbo].[sqlwatch_meta_errorlog_keyword]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_meta_errorlog_keyword_server] FOREIGN KEY([sql_instance])
REFERENCES [dbo].[sqlwatch_meta_server] ([servername])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_meta_errorlog_keyword] CHECK CONSTRAINT [fk_sqlwatch_meta_errorlog_keyword_server]
GO
ALTER TABLE [dbo].[sqlwatch_meta_errorlog_text]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_meta_errorlog_text_server] FOREIGN KEY([sql_instance])
REFERENCES [dbo].[sqlwatch_meta_server] ([servername])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_meta_errorlog_text] CHECK CONSTRAINT [fk_sqlwatch_meta_errorlog_text_server]
GO
ALTER TABLE [dbo].[sqlwatch_meta_index]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_meta_index] FOREIGN KEY([sql_instance], [sqlwatch_database_id], [sqlwatch_table_id])
REFERENCES [dbo].[sqlwatch_meta_table] ([sql_instance], [sqlwatch_database_id], [sqlwatch_table_id])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_meta_index] CHECK CONSTRAINT [fk_sqlwatch_meta_index]
GO
ALTER TABLE [dbo].[sqlwatch_meta_index_missing]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_meta_index_missing_table] FOREIGN KEY([sql_instance], [sqlwatch_database_id], [sqlwatch_table_id])
REFERENCES [dbo].[sqlwatch_meta_table] ([sql_instance], [sqlwatch_database_id], [sqlwatch_table_id])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_meta_index_missing] CHECK CONSTRAINT [fk_sqlwatch_meta_index_missing_table]
GO
ALTER TABLE [dbo].[sqlwatch_meta_master_file]  WITH CHECK ADD  CONSTRAINT [FK_sql_perf_mon_master_files_db] FOREIGN KEY([sql_instance], [sqlwatch_database_id])
REFERENCES [dbo].[sqlwatch_meta_database] ([sql_instance], [sqlwatch_database_id])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_meta_master_file] CHECK CONSTRAINT [FK_sql_perf_mon_master_files_db]
GO
ALTER TABLE [dbo].[sqlwatch_meta_memory_clerk]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_meta_memory_clerk_server] FOREIGN KEY([sql_instance])
REFERENCES [dbo].[sqlwatch_meta_server] ([servername])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_meta_memory_clerk] CHECK CONSTRAINT [fk_sqlwatch_meta_memory_clerk_server]
GO
ALTER TABLE [dbo].[sqlwatch_meta_os_volume]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_meta_os_volume_server] FOREIGN KEY([sql_instance])
REFERENCES [dbo].[sqlwatch_meta_server] ([servername])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_meta_os_volume] CHECK CONSTRAINT [fk_sqlwatch_meta_os_volume_server]
GO
ALTER TABLE [dbo].[sqlwatch_meta_performance_counter]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_meta_performance_counter_server] FOREIGN KEY([sql_instance])
REFERENCES [dbo].[sqlwatch_meta_server] ([servername])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_meta_performance_counter] CHECK CONSTRAINT [fk_sqlwatch_meta_performance_counter_server]
GO
ALTER TABLE [dbo].[sqlwatch_meta_performance_counter_instance]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_stage_performance_counters_to_collect_perf_id] FOREIGN KEY([sql_instance], [performance_counter_id])
REFERENCES [dbo].[sqlwatch_meta_performance_counter] ([sql_instance], [performance_counter_id])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_meta_performance_counter_instance] CHECK CONSTRAINT [fk_sqlwatch_stage_performance_counters_to_collect_perf_id]
GO
ALTER TABLE [dbo].[sqlwatch_meta_procedure]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_meta_procedure_server] FOREIGN KEY([sql_instance])
REFERENCES [dbo].[sqlwatch_meta_server] ([servername])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_meta_procedure] CHECK CONSTRAINT [fk_sqlwatch_meta_procedure_server]
GO
ALTER TABLE [dbo].[sqlwatch_meta_program_name]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_meta_program_name_sql_instance] FOREIGN KEY([sql_instance])
REFERENCES [dbo].[sqlwatch_meta_server] ([servername])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_meta_program_name] CHECK CONSTRAINT [fk_sqlwatch_meta_program_name_sql_instance]
GO
ALTER TABLE [dbo].[sqlwatch_meta_query_plan]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_meta_query_plan_handle_procedure] FOREIGN KEY([sql_instance], [sqlwatch_database_id], [sqlwatch_procedure_id])
REFERENCES [dbo].[sqlwatch_meta_procedure] ([sql_instance], [sqlwatch_database_id], [sqlwatch_procedure_id])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_meta_query_plan] CHECK CONSTRAINT [fk_sqlwatch_meta_query_plan_handle_procedure]
GO
ALTER TABLE [dbo].[sqlwatch_meta_query_plan_hash]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_meta_query_plan_hash_sql_instance] FOREIGN KEY([sql_instance])
REFERENCES [dbo].[sqlwatch_meta_server] ([servername])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_meta_query_plan_hash] CHECK CONSTRAINT [fk_sqlwatch_meta_query_plan_hash_sql_instance]
GO
ALTER TABLE [dbo].[sqlwatch_meta_report]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_meta_report_server] FOREIGN KEY([sql_instance])
REFERENCES [dbo].[sqlwatch_meta_server] ([servername])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_meta_report] CHECK CONSTRAINT [fk_sqlwatch_meta_report_server]
GO
ALTER TABLE [dbo].[sqlwatch_meta_server]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_meta_config_sql_instance] FOREIGN KEY([servername])
REFERENCES [dbo].[sqlwatch_config_sql_instance] ([sql_instance])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_meta_server] CHECK CONSTRAINT [fk_sqlwatch_meta_config_sql_instance]
GO
ALTER TABLE [dbo].[sqlwatch_meta_snapshot_header_baseline]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_meta_snapshot_header_baseline_meta] FOREIGN KEY([baseline_id], [sql_instance])
REFERENCES [dbo].[sqlwatch_meta_baseline] ([baseline_id], [sql_instance])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_meta_snapshot_header_baseline] CHECK CONSTRAINT [fk_sqlwatch_meta_snapshot_header_baseline_meta]
GO
ALTER TABLE [dbo].[sqlwatch_meta_sql_query]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_meta_sql_text] FOREIGN KEY([sql_instance])
REFERENCES [dbo].[sqlwatch_meta_server] ([servername])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_meta_sql_query] CHECK CONSTRAINT [fk_sqlwatch_meta_sql_text]
GO
ALTER TABLE [dbo].[sqlwatch_meta_system_configuration]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_meta_system_configuration] FOREIGN KEY([sql_instance])
REFERENCES [dbo].[sqlwatch_meta_server] ([servername])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_meta_system_configuration] CHECK CONSTRAINT [fk_sqlwatch_meta_system_configuration]
GO
ALTER TABLE [dbo].[sqlwatch_meta_system_configuration_scd]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_logger_system_configuration_scd_keyword] FOREIGN KEY([sql_instance], [sqlwatch_configuration_id])
REFERENCES [dbo].[sqlwatch_meta_system_configuration] ([sql_instance], [sqlwatch_configuration_id])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_meta_system_configuration_scd] CHECK CONSTRAINT [fk_sqlwatch_logger_system_configuration_scd_keyword]
GO
ALTER TABLE [dbo].[sqlwatch_meta_table]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_meta_table_database] FOREIGN KEY([sql_instance], [sqlwatch_database_id])
REFERENCES [dbo].[sqlwatch_meta_database] ([sql_instance], [sqlwatch_database_id])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_meta_table] CHECK CONSTRAINT [fk_sqlwatch_meta_table_database]
GO
ALTER TABLE [dbo].[sqlwatch_meta_wait_stats]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_meta_wait_stats_server] FOREIGN KEY([sql_instance])
REFERENCES [dbo].[sqlwatch_meta_server] ([servername])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_meta_wait_stats] CHECK CONSTRAINT [fk_sqlwatch_meta_wait_stats_server]
GO
ALTER TABLE [dbo].[sqlwatch_trend_perf_os_performance_counters]  WITH CHECK ADD  CONSTRAINT [fk_sqlwatch_trend_perf_os_performance_counters_meta] FOREIGN KEY([sql_instance], [performance_counter_id])
REFERENCES [dbo].[sqlwatch_meta_performance_counter] ([sql_instance], [performance_counter_id])
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[sqlwatch_trend_perf_os_performance_counters] CHECK CONSTRAINT [fk_sqlwatch_trend_perf_os_performance_counters_meta]
GO
ALTER TABLE [dbo].[sqlwatch_app_log]  WITH CHECK ADD  CONSTRAINT [chk_sqlwatch_logger_log_message_type] CHECK  (([process_message_type]='INFO' OR [process_message_type]='WARNING' OR [process_message_type]='ERROR'))
GO
ALTER TABLE [dbo].[sqlwatch_app_log] CHECK CONSTRAINT [chk_sqlwatch_logger_log_message_type]
GO
ALTER TABLE [dbo].[sqlwatch_config_action]  WITH CHECK ADD  CONSTRAINT [chk_sqlwatch_config_media_action] CHECK  (([action_exec] IS NULL AND [action_report_id] IS NOT NULL OR [action_exec] IS NOT NULL AND [action_report_id] IS NULL))
GO
ALTER TABLE [dbo].[sqlwatch_config_action] CHECK CONSTRAINT [chk_sqlwatch_config_media_action]
GO
ALTER TABLE [dbo].[sqlwatch_config_action]  WITH CHECK ADD  CONSTRAINT [chk_sqlwatch_config_media_exec] CHECK  (([action_exec_type]='T-SQL' OR [action_exec_type]='PowerShell'))
GO
ALTER TABLE [dbo].[sqlwatch_config_action] CHECK CONSTRAINT [chk_sqlwatch_config_media_exec]
GO
ALTER TABLE [dbo].[sqlwatch_config_check]  WITH CHECK ADD  CONSTRAINT [chk_sqlwatch_config_check_object_type] CHECK  (([base_object_type]='Disk' OR [base_object_type]='Job' OR [base_object_type]='Database'))
GO
ALTER TABLE [dbo].[sqlwatch_config_check] CHECK CONSTRAINT [chk_sqlwatch_config_check_object_type]
GO
ALTER TABLE [dbo].[sqlwatch_config_check_action_template]  WITH CHECK ADD  CONSTRAINT [chk_sqlwatch_config_action_template_type] CHECK  (([action_template_type]='TEXT' OR [action_template_type]='HTML'))
GO
ALTER TABLE [dbo].[sqlwatch_config_check_action_template] CHECK CONSTRAINT [chk_sqlwatch_config_action_template_type]
GO
ALTER TABLE [dbo].[sqlwatch_config_check_template]  WITH CHECK ADD  CONSTRAINT [chk_sqlwatch_config_check_template_expand_by] CHECK  (([expand_by]='Disk' OR [expand_by]='Job' OR [expand_by]='Database'))
GO
ALTER TABLE [dbo].[sqlwatch_config_check_template] CHECK CONSTRAINT [chk_sqlwatch_config_check_template_expand_by]
GO
ALTER TABLE [dbo].[sqlwatch_config_report]  WITH CHECK ADD  CONSTRAINT [chk_sqlwatch_config_report] CHECK  ((([report_definition_type]='Template' OR [report_definition_type]='HTML-Template' OR [report_definition_type]='Table' OR [report_definition_type]='HTML-Table' OR [report_definition_type]='Query') AND ([report_style_id] IS NULL AND [report_definition_type]='Query' OR [report_style_id] IS NOT NULL AND [report_definition_type]<>'Query')))
GO
ALTER TABLE [dbo].[sqlwatch_config_report] CHECK CONSTRAINT [chk_sqlwatch_config_report]
GO
ALTER TABLE [dbo].[sqlwatch_config_sql_instance]  WITH CHECK ADD  CONSTRAINT [chk_sqlwatch_config_sql_instance_is_active] CHECK  (([sql_instance]=@@servername AND [repo_collector_is_active]=(0) OR [sql_instance]<>@@servername AND ([repo_collector_is_active]=(0) OR [repo_collector_is_active]=(1))))
GO
ALTER TABLE [dbo].[sqlwatch_config_sql_instance] CHECK CONSTRAINT [chk_sqlwatch_config_sql_instance_is_active]
GO
ALTER TABLE [dbo].[sqlwatch_meta_action_queue]  WITH CHECK ADD  CONSTRAINT [chk_sqlwatch_meta_action_queue_status] CHECK  (([exec_status] IS NULL OR ([exec_status]='FAILED' OR [exec_status]='ERROR' OR [exec_status]='PROCESSING' OR [exec_status]='OK' OR [exec_status]='RETRYING')))
GO
ALTER TABLE [dbo].[sqlwatch_meta_action_queue] CHECK CONSTRAINT [chk_sqlwatch_meta_action_queue_status]
GO

SET ANSI_NULLS OFF
GO
SET QUOTED_IDENTIFIER OFF
GO
CREATE PROCEDURE [dbo].[StreamPerformanceCounters]
WITH EXECUTE AS CALLER
AS
EXTERNAL NAME [SqlWatchDatabase].[StoredProcedures].[StreamPerformanceCounters]
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_config_add_action] (
	@action_id smallint = null,
	@action_description nvarchar(max),
	@action_exec_type nvarchar(50),
	@action_exec varchar(max) = null,
	@action_report_id smallint = null,
	@action_enabled bit = 1
)
as

set xact_abort on;
set nocount on;

if @action_id < 0 --to maintain actions shipped with sqlwatch and to be able to insert negative identities
	begin
		merge [dbo].[sqlwatch_config_action] as target
		using (
			select 
				 [action_id] = @action_id
				,[action_description] = @action_description
				,[action_exec_type] = @action_exec_type
				,[action_exec] = @action_exec
				,[action_report_id] = @action_report_id
				,[action_enabled] = @action_enabled
			) as source
			on source.action_id = target.action_id

		when matched and target.[date_updated] is null
			then update
				set  [action_description] = source.[action_description]
					,[action_exec_type] = source.[action_exec_type]
					,[action_exec] = source.[action_exec]
					,[action_report_id] = source.[action_report_id]
					,[action_enabled] = source.[action_enabled]

		--if not matched or action is null we are going to insert new row
		when not matched
			then insert ( [action_id]
						 ,[action_description]
						 ,[action_exec_type]
						 ,[action_exec]
						 ,[action_report_id]
						 ,[action_enabled] )
			values ( source.[action_id]
					,source.[action_description]
					,source.[action_exec_type]
					,source.[action_exec]
					,source.[action_report_id]
					,source.[action_enabled] );
	end
else
	begin
		merge [dbo].[sqlwatch_config_action] as target
		using (
			select 
				 [action_id] = @action_id
				,[action_description] = @action_description
				,[action_exec_type] = @action_exec_type
				,[action_exec] = @action_exec
				,[action_report_id] = @action_report_id
				,[action_enabled] = @action_enabled
			) as source
			on source.action_id = target.action_id

		when matched
			then update
				set  [action_description] = source.[action_description]
					,[action_exec_type] = source.[action_exec_type]
					,[action_exec] = source.[action_exec]
					,[action_report_id] = source.[action_report_id]
					,[action_enabled] = source.[action_enabled]

		--if not matched or action is null we are going to insert new row
		when not matched
			then insert ( [action_description]
						 ,[action_exec_type]
						 ,[action_exec]
						 ,[action_report_id]
						 ,[action_enabled] )
			values ( source.[action_description]
					,source.[action_exec_type]
					,source.[action_exec]
					,source.[action_report_id]
					,source.[action_enabled] );
	end
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_config_add_check] (
	@sql_instance varchar(32) = @@SERVERNAME,
	@check_id smallint = null,
	@check_name nvarchar(50),
	@check_description nvarchar(2048),
	@check_query nvarchar(max), --the sql query to execute to check for value, the return should be a one row one value which will be compared against thresholds. 
	@check_frequency_minutes smallint = null, --how often to run this check, by default the ALERT agent job runs every 2 minutes but we may not want to run all checks every 2 minutes.
	@check_threshold_warning varchar(100) = null, --warning is optional
	@check_threshold_critical varchar(100), --but critical is not. we have to check against something. 
	@check_enabled bit = 1, --if enabled the check will be processed
	@check_action_id smallint = null, --assosiate check with actions

	--action assosiation specifics. In order to assosiate check with multiple actions, rerun the proc with new action
	@action_every_failure bit = 0,
	@action_recovery bit = 1,
	@action_repeat_period_minutes smallint = null,
	@action_hourly_limit smallint = 2,
	@action_template_id smallint = -1, --default template shipped with SQLWATCH
	@ignore_flapping bit = 0
)
as

set xact_abort on;
set nocount on;

if @check_id < 0
	begin
		merge [dbo].[sqlwatch_config_check] as target
		using ( select
				 [check_id] = @check_id
				,[check_name] = @check_name
				,[check_description] = @check_description
				,[check_query] = @check_query
				,[check_frequency_minutes] = @check_frequency_minutes
				,[check_threshold_warning] = @check_threshold_warning
				,[check_threshold_critical] = @check_threshold_critical
				,[check_enabled] = @check_enabled
				,[ignore_flapping] = @ignore_flapping
			) as source
		on source.check_id = target.check_id

		when not matched then
			insert ( [check_id]
					,[check_name]
					,[check_description]
					,[check_query]
					,[check_frequency_minutes]
					,[check_threshold_warning]
					,[check_threshold_critical]
					,[check_enabled]
					,[ignore_flapping]
				   )
			values ( source.[check_id]
					,source.[check_name]
					,source.[check_description]
					,source.[check_query]
					,source.[check_frequency_minutes]
					,source.[check_threshold_warning]
					,source.[check_threshold_critical]
					,source.[check_enabled]
					,source.[ignore_flapping])

		-- if the user sets the check as "user modified" we will not update it
		when matched and isnull(target.user_modified,0) = 0
			then update 
				set
				 [check_name] = source.[check_name]
				,[check_description] = source.[check_description]
				,[check_query] = source.[check_query]
				,[check_frequency_minutes] = source.[check_frequency_minutes]
				,[check_threshold_warning] = source.[check_threshold_warning]
				,[check_threshold_critical] = source.[check_threshold_critical]
				,[check_enabled] = source.[check_enabled]
				,[ignore_flapping] = source.[ignore_flapping]
				;
	end
else
	begin
		merge [dbo].[sqlwatch_config_check] as target
		using ( select
				 [check_id] = @check_id
				,[check_name] = @check_name
				,[check_description] = @check_description
				,[check_query] = @check_query
				,[check_frequency_minutes] = @check_frequency_minutes
				,[check_threshold_warning] = @check_threshold_warning
				,[check_threshold_critical] = @check_threshold_critical
				,[check_enabled] = @check_enabled
				,[ignore_flapping] = @ignore_flapping
			) as source
		on source.check_id = target.check_id

		when not matched then
			insert ( [check_name]
					,[check_description]
					,[check_query]
					,[check_frequency_minutes]
					,[check_threshold_warning]
					,[check_threshold_critical]
					,[check_enabled]
					,[ignore_flapping]
				   )
			values ( source.[check_name]
					,source.[check_description]
					,source.[check_query]
					,source.[check_frequency_minutes]
					,source.[check_threshold_warning]
					,source.[check_threshold_critical]
					,source.[check_enabled]
					,source.[ignore_flapping])

		when matched then
			update set
				 [check_name] = source.[check_name]
				,[check_description] = source.[check_description]
				,[check_query] = source.[check_query]
				,[check_frequency_minutes] = source.[check_frequency_minutes]
				,[check_threshold_warning] = source.[check_threshold_warning]
				,[check_threshold_critical] = source.[check_threshold_critical]
				,[check_enabled] = source.[check_enabled]
				,[ignore_flapping] = source.[ignore_flapping];

			Print 'Check (Id: ' + convert(varchar(10),@check_id) + ') updated.'
	end

if @check_action_id is not null
	begin
		merge [dbo].[sqlwatch_config_check_action] as target
		using (
				select
				 [sql_instance]=@@SERVERNAME
				,[check_id] = @check_id
				,[action_id] = @check_action_id
				,[action_every_failure] = @action_every_failure
				,[action_recovery] = @action_recovery
				,[action_repeat_period_minutes] = @action_repeat_period_minutes
				,[action_hourly_limit] = @action_hourly_limit
				,[action_template_id] = @action_template_id
			 ) as source
		on source.check_id = target.check_id
		and source.action_id = target.action_id

		when not matched then
		insert ( [check_id],[action_id]
				,[action_every_failure]
				,[action_recovery]
				,[action_repeat_period_minutes]
				,[action_hourly_limit]
				,[action_template_id])

		values (  source.[check_id]
				, source.[action_id]
				, source.[action_every_failure]
				, source.[action_recovery]
				, source.[action_repeat_period_minutes]
				, source.[action_hourly_limit]
				, source.[action_template_id])

		when matched and target.[date_updated] is null then 
			update set
				 [check_id] = source.check_id
				,[action_id] = source.action_id
				,[action_every_failure] = source.action_every_failure
				,[action_recovery] = source.action_recovery
				,[action_repeat_period_minutes] = source.action_repeat_period_minutes
				,[action_hourly_limit] = source.action_hourly_limit
				,[action_template_id] = source.action_template_id;

		Print 'Check (Id: ' + convert(varchar(10),@check_id) + ') assosiated with action (Id: ' + convert(varchar(10),@check_action_id) + ').'
	end
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_config_add_report] (
	@sql_instance varchar(32) = @@SERVERNAME,
	@report_id smallint = null,
	@report_title varchar(255) ,
	@report_description varchar(4000) = null,
	@report_definition nvarchar(max) ,
	@report_definition_type varchar(10) ,
	@report_active bit = 1,
	@report_batch_id varchar(255) = null,
	@report_style_id smallint = null ,
	--action to assosiate report with, in case of multiple actions, rerun the procedure with the same params but different action id
	@report_action_id smallint = null
)
as

set xact_abort on;
set nocount on;

set @report_style_id = case 
	when @report_style_id is null and @report_definition_type <> 'Query' then -1 
	when @report_style_id is not null and @report_definition_type = 'Query' then null
	else @report_style_id end

if @report_id < 0 
	begin
		merge [dbo].[sqlwatch_config_report] as target
		using ( select
				 [report_id] = @report_id
				,[report_title] = @report_title
				,[report_description] = @report_description
				,[report_definition] = @report_definition
				,[report_definition_type] = @report_definition_type
				,[report_active] = @report_active
				,[report_batch_id] = @report_batch_id
				,[report_style_id] = @report_style_id
		) as source
		on source.report_id = target.report_id

		when not matched then
			insert ( [report_id]
					,[report_title]
					,[report_description]
					,[report_definition]
					,[report_definition_type]
					,[report_active]
					,[report_batch_id]
					,[report_style_id])
			values ( source.[report_id]
					,source.[report_title]
					,source.[report_description]
					,source.[report_definition]
					,source.[report_definition_type]
					,source.[report_active]
					,source.[report_batch_id]
					,source.[report_style_id])

		when matched and target.[date_updated] is null then
			update
				set  [report_title] = source.[report_title]
					,[report_description] = source. [report_description]
					,[report_definition] = source.[report_definition]
					,[report_definition_type] = source.[report_definition_type]
					,[report_active] = source.[report_active]
					,[report_batch_id] = source.[report_batch_id]
					,[report_style_id] = source.[report_style_id]
		;
	end
else
	begin
		merge [dbo].[sqlwatch_config_report] as target
		using ( select
				 [report_id] = @report_id
				,[report_title] = @report_title
				,[report_description] = @report_description
				,[report_definition] = @report_definition
				,[report_definition_type] = @report_definition_type
				,[report_active] = @report_active
				,[report_batch_id] = @report_batch_id
				,[report_style_id] = @report_style_id
		) as source
		on source.report_id = target.report_id

		when not matched then
			insert ( [report_title]
					,[report_description]
					,[report_definition]
					,[report_definition_type]
					,[report_active]
					,[report_batch_id]
					,[report_style_id])
			values ( source.[report_title]
					,source.[report_description]
					,source.[report_definition]
					,source.[report_definition_type]
					,source.[report_active]
					,source.[report_batch_id]
					,source.[report_style_id])

		when matched then
			update
				set  [report_title] = source.[report_title]
					,[report_description] = source. [report_description]
					,[report_definition] = source.[report_definition]
					,[report_definition_type] = source.[report_definition_type]
					,[report_active] = source.[report_active]
					,[report_batch_id] = source.[report_batch_id]
					,[report_style_id] = source.[report_style_id]
		;

		Print 'Report (Id: ' + convert(varchar(10),@report_id) + ') updated.'
	end


if @report_action_id is not null
	begin
		insert into [dbo].[sqlwatch_config_report_action] ( [report_id] ,[action_id] )

		select [s].[report_id], [s].[action_id]
		from (
			select 
				 [report_id] = @report_id
				,[action_id] = @report_action_id
			) s

		left join [dbo].[sqlwatch_config_report_action] t
			on t.report_id = s.[report_id]
			and t.action_id = s.action_id

		where t.action_id is null

		if (@@ROWCOUNT > 0)
			begin
				Print 'Report (Id: ' + convert(varchar(10),@report_id) + ') assosiated with action (Id: ' + convert(varchar(10),@report_action_id) + ').'
			end

	end
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_config_create_default_agent_jobs]
	@remove_existing bit = 0,
	@print_WTS_command bit = 0,
	@job_owner sysname = null
AS

set nocount on;

-- check if agent is running and quit of not.
-- if the agent isnt running or if we're in express edition we dont want to raise errors just a gentle warning
-- if we are in the express edition we will be able to run collection via broker

if [dbo].[ufn_sqlwatch_get_agent_status]() = 0
	begin
		print 'SQL Agent is not running. SQLWATCH relies on Agent to collect performance data.
		The database will be deployed but you will have to deploy jobs manually once you have enabled SQL Agent.
		You can run "exec [dbo].[usp_sqlwatch_config_create_default_agent_jobs]" to create default jobs.
		If you are running Express Edition you will be able to invoke collection via broker'
		return;
	end

/* create jobs */
declare @sql varchar(max)

declare @server nvarchar(255)
set @server = @@SERVERNAME


set @sql = ''
if @remove_existing = 1
	begin
		select @sql = @sql + 'exec msdb.dbo.sp_delete_job @job_id=N''' + convert(varchar(255),job_id) + ''';' 
		from msdb.dbo.sysjobs
where name like 'SQLWATCH-%'
and name not like 'SQLWATCH-REPOSITORY-%'
		exec (@sql)
		Print 'Existing default SQLWATCH jobs deleted'
	end

set @sql = ''
create table ##sqlwatch_jobs (
	job_id tinyint identity (1,1),
	job_name sysname primary key,
	freq_type int, 
	freq_interval int, 
	freq_subday_type int, 
	freq_subday_interval int, 
	freq_relative_interval int, 
	freq_recurrence_factor int, 
	active_start_date int, 
	active_end_date int, 
	active_start_time int, 
	active_end_time int,
	job_enabled tinyint,
	)


create table ##sqlwatch_steps (
	step_name sysname,
	step_id int,
	job_name sysname,
	step_subsystem sysname,
	step_command varchar(max)
	)

declare @enabled tinyint = 1
set @enabled = case when object_id('master.dbo.sp_whoisactive') is not null or object_id('dbo.sp_whoisactive') is not null then 1 else 0 end

/* job definition must be in the right order as they are executed as part of deployment */
insert into ##sqlwatch_jobs 
			( job_name,							freq_type,	freq_interval,	freq_subday_type,	freq_subday_interval,	freq_relative_interval, freq_recurrence_factor,		active_start_date,	active_end_date,	active_start_time,	active_end_time,	job_enabled )
	values	
			('SQLWATCH-INTERNAL-CONFIG',		4,			1,				8,					1,						0,						1,							20180101,			99991231,			26,					235959,				1),

			('SQLWATCH-LOGGER-PERFORMANCE',		4,			1,				2,					10,						0,						1,							20180101,			99991231,			12,					235959,				1),
			('SQLWATCH-LOGGER-XES',				4,			1,				4,					1,						0,						1,							20180101,			99991231,			12,					235959,				1),
			('SQLWATCH-LOGGER-AG',				4,			1,				4,					1,						0,						1,							20180101,			99991231,			12,					235959,				1),

			('SQLWATCH-LOGGER-DISK-UTILISATION',4,			1,				8,					1,						0,						1,							20180101,			99991231,			437,				235959,				1),
			('SQLWATCH-LOGGER-INDEXES',			4,			1,				1,					24,						0,						1,							20180101,			99991231,			1500,				235959,				1),
			('SQLWATCH-LOGGER-AGENT-HISTORY',	4,			1,				4,					10,						0,						1,							20180101,			99991231,			0,					235959,				1),

			('SQLWATCH-INTERNAL-RETENTION',		4,			1,				8,					1,						0,						1,							20180101,			99991231,			20,					235959,				1),
			('SQLWATCH-INTERNAL-TRENDS',		4,			1,				4,					60,						0,						1,							20180101,			99991231,			150,				235959,				1),

			('SQLWATCH-INTERNAL-ACTIONS',		4,			1,				2,					15,						0,						1,							20180101,			99991231,			2,					235959,				1),

			('SQLWATCH-REPORT-AZMONITOR',		4,			1,				4,					10,						0,						1,							20180101,			99991231,			21,					235959,				1),
			('SQLWATCH-LOGGER-WHOISACTIVE',		4,			1,				2,					15,						0,						0,							20180101,			99991231,			0,					235959,				@enabled),

			('SQLWATCH-INTERNAL-CHECKS',		4,			1,				4,					1,						0,						1,							20180101,			99991231,			43,					235959,				1),
			('SQLWATCH-LOGGER-SYSCONFIG',		4,			1,				1,					1,						0,						1,							20180101,			99991231,			0,					235959,				1)

			--('SQLWATCH-USER-REPORTS',			4,		1,			1,				0,				0,					1,					20180101,	99991231, 80000,		235959,		1)
			,('SQLWATCH-LOGGER-PROCS',			4,			1,				4,					10,						0,						0,							20180101,			99991231,			30,					235959,				1)

/* step definition */

/*  Normally, the SQLWATCH-INTERNAL-META-CONFIG runs any metadata config procedures that collect reference data every hour. by reference data
	we mean list of databases, tables, jobs, indexes etc. this is to reduce load during more frequent collectors such as the performance collector.
	For obvious reasons, we would not want to collect list of tables every minute as that would be pointless however, in case of less frequent jobs such as disk collector 
	and index collection, or those more time consuming and resource heavy, by exception, we will run meta data collection part of the data collector job rather than the standard meta-config job 
	SQLWATCH tries to be as lightweight as possible and will not collect any data unles required.
*/

insert into ##sqlwatch_steps
			/* step name											step_id,	job_name							subsystem,	command */
	values	('dbo.usp_sqlwatch_logger_whoisactive',					1,			'SQLWATCH-LOGGER-WHOISACTIVE',		'TSQL',		'exec dbo.usp_sqlwatch_logger_whoisactive'),

			('dbo.usp_sqlwatch_logger_performance',					1,			'SQLWATCH-LOGGER-PERFORMANCE',		'TSQL',		'exec dbo.usp_sqlwatch_logger_performance'),
			('dbo.usp_sqlwatch_logger_requests_and_sessions',		2,			'SQLWATCH-LOGGER-PERFORMANCE',		'TSQL',		'exec dbo.usp_sqlwatch_logger_requests_and_sessions'),
			('dbo.usp_sqlwatch_logger_xes_blockers',				3,			'SQLWATCH-LOGGER-PERFORMANCE',		'TSQL',		'exec dbo.usp_sqlwatch_logger_xes_blockers'),
			
			('dbo.usp_sqlwatch_logger_xes_waits',					1,			'SQLWATCH-LOGGER-XES',				'TSQL',		'exec dbo.usp_sqlwatch_logger_xes_waits'),
			('dbo.usp_sqlwatch_logger_xes_diagnostics',				2,			'SQLWATCH-LOGGER-XES',				'TSQL',		'exec dbo.usp_sqlwatch_logger_xes_diagnostics'),
			('dbo.usp_sqlwatch_logger_xes_long_queries',			3,			'SQLWATCH-LOGGER-XES',				'TSQL',		'exec dbo.usp_sqlwatch_logger_xes_long_queries'),

			('dbo.usp_sqlwatch_logger_hadr_database_replica_states',1,			'SQLWATCH-LOGGER-AG',				'TSQL',		'exec dbo.usp_sqlwatch_logger_hadr_database_replica_states'),


			('1 minute trend',										1,			'SQLWATCH-INTERNAL-TRENDS',			'TSQL',		'exec dbo.usp_sqlwatch_trend_perf_os_performance_counters @interval_minutes = 1, @valid_days = 7'),
			('5 minutes trend',										2,			'SQLWATCH-INTERNAL-TRENDS',			'TSQL',		'exec dbo.usp_sqlwatch_trend_perf_os_performance_counters @interval_minutes = 5, @valid_days = 90'),
			('60 minutes trend',									3,			'SQLWATCH-INTERNAL-TRENDS',			'TSQL',		'exec dbo.usp_sqlwatch_trend_perf_os_performance_counters @interval_minutes = 60, @valid_days = 720'),

			--('dbo.usp_sqlwatch_internal_process_reports',1,			'SQLWATCH-USER-REPORTS',			'TSQL',		'exec dbo.usp_sqlwatch_internal_process_reports @report_batch_id = 1'),

			('dbo.usp_sqlwatch_internal_process_checks',			1,			'SQLWATCH-INTERNAL-CHECKS',			'TSQL',		'exec dbo.usp_sqlwatch_internal_process_checks'),
			('dbo.usp_sqlwatch_internal_process_reports',			1,			'SQLWATCH-REPORT-AZMONITOR',		'TSQL',		'exec dbo.usp_sqlwatch_internal_process_reports @report_batch_id = ''AzureLogMonitor-1'''),


			('Process Actions',										1,			'SQLWATCH-INTERNAL-ACTIONS',		'PowerShell','
$output = "x"
while ($output -ne $null) { 
	$output = Invoke-SqlCmd -ServerInstance "' + @server + '" -Database ' + 'SQLWATCH' + ' -MaxCharLength 2147483647 -Query "exec [dbo].[usp_sqlwatch_internal_action_queue_get_next]"

	$status = ""
	$queue_item_id = $output.queue_item_id
    $operation = ""
	$ErrorOutput = ""
	$MsgType = "OK"
	
	if ( $output -ne $null) {
		if ( $output.action_exec_type -eq "T-SQL" ) {
			try {
				$ErrorOutput = Invoke-SqlCmd -ServerInstance "' + @server + '" -Database ' + 'SQLWATCH' + ' -ErrorAction "Stop" -Query $output.action_exec -MaxCharLength 2147483647
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
		Invoke-SqlCmd -ServerInstance "' + @server + '" -Database ' + 'SQLWATCH' + ' -ErrorAction "Stop" -Query "exec [dbo].[usp_sqlwatch_internal_action_queue_update]
					@queue_item_id = $queue_item_id,
					@error = ''$ErrorOutput'',
					@exec_status = ''$MsgType''"
	}
}'),
			
			('dbo.usp_sqlwatch_logger_agent_job_history', 1,		'SQLWATCH-LOGGER-AGENT-HISTORY',	'TSQL',		'exec dbo.usp_sqlwatch_logger_agent_job_history'),

			('dbo.usp_sqlwatch_internal_retention',		1,			'SQLWATCH-INTERNAL-RETENTION',		'TSQL',		'exec dbo.usp_sqlwatch_internal_retention'),
			('dbo.usp_sqlwatch_internal_purge_deleted_items',2,		'SQLWATCH-INTERNAL-RETENTION',		'TSQL',		'exec dbo.usp_sqlwatch_internal_purge_deleted_items'),

			('dbo.usp_sqlwatch_logger_disk_utilisation',1,			'SQLWATCH-LOGGER-DISK-UTILISATION',	'TSQL',		'exec dbo.usp_sqlwatch_logger_disk_utilisation'),
			('dbo.usp_sqlwatch_logger_disk_utilisation_table',3,	'SQLWATCH-LOGGER-DISK-UTILISATION', 'TSQL',		'exec dbo.usp_sqlwatch_logger_disk_utilisation_table'),

			('Get-WMIObject Win32_Volume',		2,					'SQLWATCH-LOGGER-DISK-UTILISATION',	'PowerShell', N'
#https://msdn.microsoft.com/en-us/library/aa394515(v=vs.85).aspx
#driveType 3 = Local disk
Get-WMIObject Win32_Volume | ?{$_.DriveType -eq 3 -And $_.Name -notlike "\\?\Volume*" } | %{
    $VolumeName = $_.Name
    $FreeSpace = $_.Freespace
    $Capacity = $_.Capacity
    $VolumeLabel = $_.Label
    $FileSystem = $_.Filesystem
    $BlockSize = $_.BlockSize
    Invoke-SqlCmd -ServerInstance "' + @server + '" -Database ' + 'SQLWATCH' + ' -Query "
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
}'),

			('dbo.usp_sqlwatch_internal_add_index',			1,		'SQLWATCH-LOGGER-INDEXES',		'TSQL', 'exec dbo.usp_sqlwatch_internal_add_index'),
			('dbo.usp_sqlwatch_internal_add_index_missing',	2,		'SQLWATCH-LOGGER-INDEXES',		'TSQL', 'exec dbo.usp_sqlwatch_internal_add_index_missing'),	
			('dbo.usp_sqlwatch_logger_missing_index_stats',	3,		'SQLWATCH-LOGGER-INDEXES',		'TSQL', 'exec dbo.usp_sqlwatch_logger_missing_index_stats'),
			('dbo.usp_sqlwatch_logger_index_usage_stats',	4,		'SQLWATCH-LOGGER-INDEXES',		'TSQL', 'exec dbo.usp_sqlwatch_logger_index_usage_stats'),
			('dbo.usp_sqlwatch_logger_index_histogram',		5,		'SQLWATCH-LOGGER-INDEXES',		'TSQL', 'exec dbo.usp_sqlwatch_logger_index_histogram'),
			
			('dbo.usp_sqlwatch_internal_add_database',				1,	'SQLWATCH-INTERNAL-CONFIG','TSQL', 'exec dbo.usp_sqlwatch_internal_add_database'),
			('dbo.usp_sqlwatch_internal_add_master_file',			2,	'SQLWATCH-INTERNAL-CONFIG','TSQL', 'exec dbo.usp_sqlwatch_internal_add_master_file'),
			('dbo.usp_sqlwatch_internal_add_table',					3,	'SQLWATCH-INTERNAL-CONFIG','TSQL', 'exec dbo.usp_sqlwatch_internal_add_table'),
			('dbo.usp_sqlwatch_internal_add_job',					4,	'SQLWATCH-INTERNAL-CONFIG','TSQL', 'exec dbo.usp_sqlwatch_internal_add_job'),
			('dbo.usp_sqlwatch_internal_add_performance_counter',	5,	'SQLWATCH-INTERNAL-CONFIG','TSQL', 'exec dbo.usp_sqlwatch_internal_add_performance_counter'),
			('dbo.usp_sqlwatch_internal_add_memory_clerk',			6,	'SQLWATCH-INTERNAL-CONFIG','TSQL', 'exec dbo.usp_sqlwatch_internal_add_memory_clerk'),
			('dbo.usp_sqlwatch_internal_add_wait_type',				7,	'SQLWATCH-INTERNAL-CONFIG','TSQL', 'exec dbo.usp_sqlwatch_internal_add_wait_type'),
			('dbo.usp_sqlwatch_internal_expand_checks',				8,	'SQLWATCH-INTERNAL-CONFIG','TSQL', 'exec dbo.usp_sqlwatch_internal_expand_checks'),
			('dbo.usp_sqlwatch_internal_add_procedure',				9,	'SQLWATCH-INTERNAL-CONFIG','TSQL', 'exec dbo.usp_sqlwatch_internal_add_procedure'),
			
			
			('dbo.usp_sqlwatch_internal_add_system_configuration',	1,	'SQLWATCH-LOGGER-SYSCONFIG','TSQL', 'exec dbo.usp_sqlwatch_internal_add_system_configuration'),
			('dbo.usp_sqlwatch_logger_system_configuration',	    2,	'SQLWATCH-LOGGER-SYSCONFIG','TSQL', 'exec dbo.usp_sqlwatch_logger_system_configuration')

			,('dbo.usp_sqlwatch_logger_procedure_stats',		1,		'SQLWATCH-LOGGER-PROCS',		'TSQL', 'exec dbo.usp_sqlwatch_logger_procedure_stats')


	if [dbo].[ufn_sqlwatch_get_config_value] ( 13 , null ) = 0
		begin
			exec [dbo].[usp_sqlwatch_internal_create_agent_job]
				@print_WTS_command = @print_WTS_command, @job_owner = @job_owner
		end
	else
		begin
			Print 'This SQLWATCH instance is using broker for data collection. Jobs will not be deployed'
		end
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_config_repository_add_remote_instance]
	@sql_instance varchar(32),
	@hostname nvarchar(32) = null,
	@sql_port int = null,
	@sqlwatch_database_name sysname,
	@environment sysname,
	@linked_server_name nvarchar(255) = null,
	@rmtuser nvarchar(128) = null,
	@rmtpassword nvarchar(128) = null
as


if @sql_instance = @@SERVERNAME
	begin
		raiserror ('Remote Instance is the same as local instance',16,1)
	end

merge [dbo].[sqlwatch_config_sql_instance] as target
using (
	select
		[sql_instance] = @sql_instance,
		[hostname] = @hostname,
		[sql_port] = @sql_port,
		[sqlwatch_database_name] = @sqlwatch_database_name,
		[environment] = @environment,
		[linked_server_name] = @linked_server_name,
		[repo_collector_is_active] = 1
	) as source

on source.sql_instance = target.sql_instance

when not matched then
	insert ([sql_instance],[hostname],[sql_port],[sqlwatch_database_name],[environment],[repo_collector_is_active],[linked_server_name])
	values (source.[sql_instance],source.[hostname],source.[sql_port],source.[sqlwatch_database_name],source.[environment],source.[repo_collector_is_active],source.[linked_server_name]);

IF @@ROWCOUNT > 0
	begin
		Print 'Added Remote SQL Instane (' + @sql_instance + ') to central repository.
If you are using linked server for data collection, please make sure these are also created. If you are using SSIS there is no more setup required.'
	end

if @linked_server_name is not null
	begin
		exec [dbo].[usp_sqlwatch_config_repository_create_linked_server]
			@sql_instance  = @sql_instance,
			@linked_server = @linked_server_name,
			@rmtuser = @rmtuser,
			@rmtpassword = @rmtpassword
	end
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_config_repository_create_agent_jobs]
	@threads tinyint = 1,
	@remove_existing bit = 0,
	@print_WTS_command bit = 0
as

begin
/*
-------------------------------------------------------------------------------------------------------------------
 Procedure:
	usp_sqlwatch_config_set_repository_agent_jobs

 Description:
	Creates default SQLWATCH Agent jobs for the central repostory collector via linked server. Only required
	when collectint data from remotes via linked server. NOT required when using SSIS. 

 Parameters
	@remove_existing	-	Force delete jobs so they can be re-created.
	@print_WTS_command	-	Print Command to create equivalent tasks in Windows Task scheduler for editions that have no
							SQL Agent i.e. Express.
	@threads			-	number of worker (thread) jobs to create.
	
 Author:
	Marcin Gminski

 Change Log:
	1.0		2019-12-25	- Marcin Gminski, Initial version
-------------------------------------------------------------------------------------------------------------------
*/

	set nocount on;

	declare @sql varchar(max) = '',
			@server nvarchar(255) = @@SERVERNAME,
			@enabled tinyint = 1,
			@threads_count tinyint = 0,
			@job_name sysname,
			@start_time int = 6

	if @remove_existing = 1
		begin
			select @sql = @sql + 'exec msdb.dbo.sp_delete_job @job_id=N''' + convert(varchar(255),job_id) + ''';' 
			from msdb.dbo.sysjobs
	where name like 'SQLWATCH-REPOSITORY-%'
			exec (@sql)
			Print 'Existing SQLWATCH repository jobs (SQLWATCH-REPOSITORY-%) deleted'
		end


	create table ##sqlwatch_jobs (
		job_id tinyint identity (1,1),
		job_name sysname primary key,
		freq_type int, 
		freq_interval int, 
		freq_subday_type int, 
		freq_subday_interval int, 
		freq_relative_interval int, 
		freq_recurrence_factor int, 
		active_start_date int, 
		active_end_date int, 
		active_start_time int, 
		active_end_time int,
		job_enabled tinyint,
		)


	create table ##sqlwatch_steps (
		step_name sysname,
		step_id int,
		job_name sysname,
		step_subsystem sysname,
		step_command varchar(max)
		)

insert into ##sqlwatch_jobs

			/* JOB_NAME						freq:		type,	interval,	subday_type,	subday_intrval, relative_interval,	recurrence_factor,	start_date, end_date, start_time,	end_time,	enabled */
	values	('SQLWATCH-REPOSITORY-IMPORT-ENQUEUE',		4,		1,			4,				1,				0,					1,					20180101,	99991231, @start_time,	235959,		@enabled)

insert into ##sqlwatch_steps
			/* step name											step_id,	job_name								subsystem,	command */
	values	('dbo.usp_sqlwatch_repository_remote_table_enqueue',		1,			'SQLWATCH-REPOSITORY-IMPORT-ENQUEUE',	'TSQL',		'exec dbo.usp_sqlwatch_repository_remote_table_enqueue')


while @threads_count < @threads
	begin
		set @threads_count = @threads_count + 1
		set @start_time = @start_time + 1
		set @job_name = 'SQLWATCH-REPOSITORY-IMPORT-T' + convert(varchar(10),@threads_count)
		insert into ##sqlwatch_jobs

					/* JOB_NAME		freq:		type,	interval,	subday_type,	subday_intrval, relative_interval,	recurrence_factor,	start_date, end_date, start_time,	end_time,	enabled */
			values	(@job_name,					4,		1,			4,				1,				0,					1,					20180101,	99991231, @start_time,	235959,		@enabled)

		insert into ##sqlwatch_steps
					/* step name													step_id,	job_name	subsystem,	command */
			values	('dbo.usp_sqlwatch_repository_remote_table_import',		1,		@job_name,	'TSQL',		'exec dbo.usp_sqlwatch_repository_remote_table_import')
	end

exec [dbo].[usp_sqlwatch_internal_create_agent_job] @print_WTS_command = @print_WTS_command

end
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_config_repository_create_linked_server]
	@sql_instance varchar(32) = null,
	@linked_server nvarchar(128) = null,
	@rmtuser nvarchar(128) = null,
	@rmtpassword nvarchar(128) = null
as

	if @rmtuser is not null and @rmtpassword is null
		begin
			raiserror ('@rmtpassword must be specified when @rmtuser is specified.',16,1)
		end

	set xact_abort on;
	set nocount on;
	
	declare @table_name nvarchar(max),
			@table_schema nvarchar(max),
			@sql_1 nvarchar(max),
			@hostname nvarchar(max),
			@error_message nvarchar(max),
			@sqlwatch_database_name nvarchar(max),
			@has_errors bit = 0


	--create required linked servers here:
	declare cur_ls cursor for
	select sql_instance
	from [dbo].[sqlwatch_config_sql_instance]
	where [repo_collector_is_active] = 1
	and sql_instance = isnull(@sql_instance,sql_instance)
	and sql_instance <> @@SERVERNAME

	open cur_ls

	fetch next from cur_ls into @sql_instance

	while @@FETCH_STATUS = 0
		begin

			--if no linked server given or if the linked server has been set in the previou iteration
			--this should skip if the linked server is given by user.
			if @linked_server is null or @linked_server like 'SQLWATCH-REMOTE-%'
				begin
					set @linked_server = 'SQLWATCH-REMOTE-' + @sql_instance
				end

			/* if no linked servers in the config table, add it first */
			update dbo.sqlwatch_config_sql_instance
				set linked_server_name = @linked_server
			where  linked_server_name is null
			and sql_instance = @sql_instance

			select @hostname = isnull(hostname, sql_instance), @sqlwatch_database_name = sqlwatch_database_name, @linked_server = linked_server_name
			from [dbo].[sqlwatch_config_sql_instance]
			where sql_instance = @sql_instance

			if exists (
				select * from sys.servers
				where name = @linked_server
				and is_linked = 1
				)
				begin
					exec dbo.sp_dropserver @server=@linked_server, @droplogins='droplogins'
				end

			--sp_addlinkedserver cannot be executed within a user-defined transaction.
			--https://docs.microsoft.com/en-us/sql/relational-databases/system-stored-procedures/sp-addlinkedserver-transact-sql

			exec dbo.sp_addlinkedserver @server = @linked_server, @srvproduct=N'', @provider=N'SQLNCLI11', @datasrc=@hostname
			exec dbo.sp_addlinkedsrvlogin @rmtsrvname = @linked_server , @locallogin = NULL , @useself = N'False', @rmtuser = @rmtuser, @rmtpassword = @rmtpassword
			exec dbo.sp_serveroption @server=@linked_server, @optname=N'connect timeout', @optvalue=N'60'

			Print 'Created Linked Server (' + @linked_server + ') for ' + @sql_instance + ' (' + @hostname + ')'

			fetch next from cur_ls into @sql_instance
		end
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_config_sqlserver_set_blocked_proc_threshold]
	@threshold_seconds int = 5
AS
exec sp_configure 'show advanced options', 1 ;  
RECONFIGURE ;  
exec sp_configure 'blocked process threshold', @threshold_seconds ;  
RECONFIGURE ;
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_internal_action_queue_get_next]
as
set xact_abort on
begin tran
	;with cte_get_message as (
		select top 1 *
		from [dbo].[sqlwatch_meta_action_queue]
		where [exec_status] is null
			--try reprocess previously faild items every 5 minutes
			or ([exec_status] = 'RETRYING' and datediff(minute,isnull([exec_time_end],'1970-01-01'),sysdatetime()) > 5)
		order by [time_queued]
	)
	update cte_get_message
		set [exec_status] = 'PROCESSING',
			[exec_time_start] = sysdatetime()
			--[action_exec] = replace(
			--					replace([action_exec],'{ACTION_EXEC_TIME}',convert(varchar(23),getdate(),121))
			--					,'{ACTION_EXEC_UTCTIME}',convert(varchar(23),getutcdate(),121)
			--					)
		output 
			  deleted.[action_exec], deleted.[action_exec_type], deleted.[queue_item_id]
commit tran
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_internal_action_queue_update]
	@queue_item_id bigint,
	@exec_status varchar(50),
	@error nvarchar(max) = null
as
begin

	update [dbo].[sqlwatch_meta_action_queue] 
		set [exec_status] = case when @exec_status = 'ERROR' and isnull([retry_count],0) < 5 then 'RETRYING' else @exec_status end, --try retry errors up to 5 attempts
			[exec_time_end] = sysdatetime(),
			[retry_count] = case when @exec_status = 'ERROR' then isnull([retry_count],0) + 1 else [retry_count] end --increase retry counter
	where queue_item_id = @queue_item_id

	Print 'exec_status: ' + @exec_status + ' (queue_item_id: ' + convert(varchar(100),@queue_item_id) + ')'

	set @exec_status = case when @exec_status = 'OK' then 'INFO' else @exec_status end

	if @exec_status = 'INFO' and @error = ''
		begin
			--nothing to log, not point logging blank info message as the action status will show OK.
			return 
		end
	else
		begin
			set @error = @error + ' (@queue_item_id: ' + convert(varchar(100),@queue_item_id) + ')'
			exec [dbo].[usp_sqlwatch_internal_log]
					@proc_id = @@PROCID,
					@process_stage = '6DC68414-915F-4B52-91B6-4D0B6018243B',
					@process_message = @error,
					@process_message_type = @exec_status
		end
end
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_internal_add_check]
	@check_name nvarchar(50) ,
	@check_description nvarchar(2048) ,
	@check_query nvarchar(max)  ,
	@check_frequency_minutes smallint ,
	@check_threshold_warning varchar(100) ,
	@check_threshold_critical varchar(100) ,
	@check_enabled bit = 1,
	@notification_target_id smallint ,
	@notification_enabled bit = 1,
	@notify_every_failure bit = 0,
	@notify_recovery bit = 1,
	@notification_repeat_period_minutes smallint
as

declare @checks as table(
	[sql_instance] varchar(32) not null default @@SERVERNAME,
	[check_name] nvarchar(50) not null,
	[check_description] nvarchar(2048) null,
	[check_query] nvarchar(max) not null,
	[check_frequency_minutes] smallint null,
	[check_threshold_warning] varchar(100) null,
	[check_threshold_critical] varchar(100) null,
	[check_enabled] bit not null default 1,
	[notification_target_id] smallint null,
	[notification_enabled] bit not null default 1,
	[notify_every_failure] bit not null default 0,
	[notify_recovery] bit not null default 1,
	[notification_repeat_period_minutes] smallint null
	primary key clustered (
		[check_name]
	)
) 

--insert into @checks
--select [sql_instance] = @@SERVERNAME
--	,  [check_name] = @check_name
--	,  [check_description] = @check_description
--	,  [check_query] = @check_query
--	,  [check_frequency_minutes] = @check_frequency_minutes
--	,  [check_threshold_warning] = @check_threshold_warning
--	,  [check_threshold_critical] = @check_threshold_critical
--	,  [check_enabled] = @check_enabled
--	,  [notification_target_id] = @notification_target_id
--	,  [notification_enabled] = @notification_enabled
--	,  [notify_every_failure] = @notify_every_failure
--	,  [notify_recovery]= @notify_recovery
--	,  [notification_repeat_period_minutes] = @notification_repeat_period_minutes



--merge [dbo].[sqlwatch_config_check] as target
--using @checks as source
--on source.sql_instance = target.sql_instance
--and source.check_name = target.check_name
--and source.check_query = target.check_query
----when not matched by source then 
----	delete
--when not matched by target then
--	insert ([sql_instance],
--			[check_name] ,
--			[check_description] ,
--			[check_query] ,
--			[check_frequency_minutes],
--			[check_threshold_warning],
--			[check_threshold_critical],
--			[check_enabled],
--			[delivery_target_id],
--			[delivery_enabled],
--			[deliver_every_failure],
--			[deliver_recovery],
--			[delivery_repeat_period_minutes])
--	values (source.[sql_instance],
--			source.[check_name] ,
--			source.[check_description] ,
--			source.[check_query] ,
--			source.[check_frequency_minutes],
--			source.[check_threshold_warning],
--			source.[check_threshold_critical],
--			source.[check_enabled],
--			source.[notification_target_id],
--			source.[notification_enabled],
--			source.[notify_every_failure],
--			source.[notify_recovery],
--			source.[notification_repeat_period_minutes]);
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_internal_add_database]
as
	set nocount on;

	/*	using database_create_data to distinguish databases that have been dropped and re-created 
		this is particulary useful when doing performance testing and we are re-creating test databases throughout the process and want to compare them later.
		However, with every SQL Server restart, tempdb will be recreated and will have new date_created. On some dev and uat servers we may end up with dozen or more
		tempdbs. To account for this, we are going to default the create_date for tempdb to '1970-01-01'
	*/
	;merge [dbo].[sqlwatch_meta_database] as target
	using (
		select [name], [create_date] = case when [name] = 'tempdb' then convert(datetime,'1970-01-01') else [create_date] end, [sql_instance]
			, [is_auto_close_on], [is_auto_shrink_on], [is_auto_update_stats_on]
			, [user_access], [state], [snapshot_isolation_state] , [is_read_committed_snapshot_on] 
			, [recovery_model] , [page_verify_option] 
			--, BC = binary_checksum([is_auto_close_on], [is_auto_shrink_on], [is_auto_update_stats_on]
			--					 , [user_access], [state], [snapshot_isolation_state] , [is_read_committed_snapshot_on] 
			--				 	 , [recovery_model] , [page_verify_option])
		from dbo.vw_sqlwatch_sys_databases
		--union all
		/* mssqlsystemresource database appears in the performance counters
		so we need it as a dimensions to be able to filter in the report */
		--select 'mssqlsystemresource', '1970-01-01', @@SERVERNAME
		--	, null, null, null, null, null, null, null, null, null, null
	) as source
		on (
				source.[name] = target.[database_name] collate database_default
			and source.[create_date] = target.[database_create_date]
			and source.[sql_instance] = target.[sql_instance] collate database_default
		)
	/* dropped databases are going to be updated to current = 0 */
	when not matched by source and target.sql_instance = @@SERVERNAME then
		update set [is_current] = 0

	when matched then
		update set [is_current] = 1,
				[date_last_seen] = case when datediff(hour,[date_last_seen],getutcdate()) >= 24 then getutcdate() else [date_last_seen] end,
				[is_auto_close_on] = source.[is_auto_close_on],
				[is_auto_shrink_on] = source.[is_auto_shrink_on],
				[is_auto_update_stats_on] = source.[is_auto_update_stats_on],
				[user_access] = source.[user_access],
				[snapshot_isolation_state] = source.[snapshot_isolation_state],
				[is_read_committed_snapshot_on] = source.[is_read_committed_snapshot_on],
				[recovery_model] = source.[recovery_model],
				[page_verify_option] = source.[page_verify_option]
			
	when not matched by target then
		insert ([database_name], [database_create_date], [sql_instance], [is_auto_close_on], [is_auto_shrink_on], [is_auto_update_stats_on]
			,[user_access], [state], [snapshot_isolation_state], [is_read_committed_snapshot_on], [recovery_model], [page_verify_option]
			,[date_last_seen], [is_current]
		)
		values (source.[name], source.[create_date], source.[sql_instance]
			, source.[is_auto_close_on], source.[is_auto_shrink_on], source.[is_auto_update_stats_on]
			, source.[user_access], source.[state], source.[snapshot_isolation_state]
			, source.[is_read_committed_snapshot_on], source.[recovery_model]
			, source.[page_verify_option]
			, getutcdate(), 1
			);
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_internal_add_index] (
	@databases varchar(max) = '-tempdb'
)
as
/*
-------------------------------------------------------------------------------------------------------------------
 Procedure:
	usp_sqlwatch_internal_add_index

 Description:
	Builds meta reference table with all indexes from each database so we can alloate internal sqlwatchid

 Parameters
	None

 Author:
	Marcin Gminski

 Change Log:
	1.0		2019-08		- Marcin Gminski, Initial version
	1.1		2019-12-10	- Fix https://github.com/marcingminski/sqlwatch/issues/130, HEAPs have NULL name in sys.indexes
						  but for our purpose we are making it inherit table name.
	1.2		2020-06-26	- Marcin Gminski - fix https://github.com/marcingminski/sqlwatch/issues/177
-------------------------------------------------------------------------------------------------------------------
*/
set nocount on;

if @databases = ''
	begin
		set @databases = '-tempdb'
	end

create table ##DB61B2CD92324E4B89019FFA7BEF1010 (
	index_name nvarchar(128), 
	index_id int,
	index_type_desc nvarchar(128),
	[table_name] nvarchar(512),
	[database_name] nvarchar(128),
	sqlwatch_database_id smallint null,
	sqlwatch_table_id int null
)


create unique clustered index icx_tmp_DB61B2CD92324E4B89019FFA7BEF1010 
	on ##DB61B2CD92324E4B89019FFA7BEF1010 ([table_name],[database_name],index_id)

--https://github.com/marcingminski/sqlwatch/issues/177
declare @sqlwatch_sys_databases table (
	[name] [sysname] NOT NULL,
	[create_date] [datetime] NOT NULL,
	UNIQUE ([name],[create_date])
)

insert into @sqlwatch_sys_databases
SELECT name, create_date
FROM [dbo].[vw_sqlwatch_sys_databases]

insert into ##DB61B2CD92324E4B89019FFA7BEF1010 (index_name, index_id, index_type_desc, [table_name], [database_name])
exec [dbo].[usp_sqlwatch_internal_foreachdb] @databases = @databases, @command = 'use [?]
insert into ##DB61B2CD92324E4B89019FFA7BEF1010 (index_name, index_id, index_type_desc, [table_name], [database_name])
select isnull(ix.name,object_name(ix.object_id)), ix.index_id, ix.type_desc, s.name + ''.'' + t.name, ''?''
from sys.indexes ix
inner join sys.tables t 
	on t.[object_id] = ix.[object_id]
inner join sys.schemas s 
	on s.[schema_id] = t.[schema_id]
where objectproperty( ix.object_id, ''IsMSShipped'' ) = 0 ', @calling_proc_id = @@PROCID

update t
	set sqlwatch_database_id = md.sqlwatch_database_id, 
	sqlwatch_table_id = mt.sqlwatch_table_id
from ##DB61B2CD92324E4B89019FFA7BEF1010 t

inner join [dbo].[sqlwatch_meta_database] md
	on md.[database_name] = t.[database_name] collate database_default
	and md.sql_instance = @@SERVERNAME

inner join [dbo].[sqlwatch_meta_table] mt
	on mt.table_name = t.table_name collate database_default
	and mt.sqlwatch_database_id = md.sqlwatch_database_id
	and mt.sql_instance = md.sql_instance

inner join @sqlwatch_sys_databases dbs
	on dbs.name = md.database_name collate database_default
	and dbs.create_date = md.database_create_date

merge [dbo].[sqlwatch_meta_index] as target
	using ##DB61B2CD92324E4B89019FFA7BEF1010 as source
on target.sqlwatch_database_id = source.sqlwatch_database_id
and target.sqlwatch_table_id = source.sqlwatch_table_id
and target.sql_instance = @@SERVERNAME
and target.index_name = source.index_name collate database_default

when not matched by source and target.sql_instance = @@SERVERNAME then
	update set [is_record_deleted] = 1

when matched then
	update set [date_last_seen] = getutcdate(),
		[is_record_deleted] = 0,
		index_id = case when source.index_id <> target.index_id then source.index_id else target.index_id end,
		index_type_desc = case when source.index_type_desc <> target.index_type_desc collate database_default then source.index_type_desc else target.index_type_desc end collate database_default,
		date_updated = case when source.index_id <> target.index_id or source.index_type_desc <> target.index_type_desc collate database_default then GETUTCDATE() else date_updated end

--when not matched by source and target.sql_instance = @@SERVERNAME then
--	update set date_deleted = GETUTCDATE()

	                           --a new index could have been added since we collected tables.
when not matched by target and source.sqlwatch_table_id is not null then
	insert ([sql_instance],[sqlwatch_database_id],[sqlwatch_table_id],[index_id],[index_type_desc],[index_name])
	values (@@SERVERNAME,source.[sqlwatch_database_id],source.[sqlwatch_table_id],source.[index_id],source.[index_type_desc],source.[index_name]);
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_internal_add_index_missing]
	@databases varchar(max) = '-tempdb,-master,-msdb,-%ReportServer%',
	@ignore_global_exclusion bit = 0
as

declare @database_name sysname,
		@database_create_date datetime,
		@sql nvarchar(max)

create table #t (
		[database_name] nvarchar(128), 
		[create_date] datetime,
		[table_name] nvarchar(512),
		[equality_columns]  nvarchar(max),
		[inequality_columns] nvarchar(max),
		[included_columns] nvarchar(max),
		[statement] nvarchar(max),
		[index_handle] int
)

create clustered index idx_tmp_t on #t ([database_name], [create_date], [table_name], [index_handle])


insert into #t
exec [dbo].[usp_sqlwatch_internal_foreachdb] @databases = @databases, @command = 'use [?]
select
	[database_name] = ''?'',
	[create_date] = db.create_date,
	[table_name] = s.name + ''.'' + t.name,
	[equality_columns] ,
	[inequality_columns] ,
	[included_columns] ,
	[statement] ,
	id.[index_handle]
from sys.dm_db_missing_index_details id
inner join [?].sys.tables t
	on t.object_id = id.object_id
inner join [?].sys.schemas s
	on t.schema_id = s.schema_id
inner join sys.databases db
	on db.name = ''?''
	', @calling_proc_id = @@PROCID, @ignore_global_exclusion = @ignore_global_exclusion

merge [dbo].[sqlwatch_meta_index_missing] as target
using (
select
		[sql_instance] = @@SERVERNAME,
		db.[sqlwatch_database_id] ,
		mt.[sqlwatch_table_id] ,
		idx.[equality_columns] ,
		idx.[inequality_columns] ,
		idx.[included_columns] ,
		idx.[statement] ,
		idx.[index_handle] ,
		[date_added] = getdate()
	from #t idx

	inner join [dbo].[sqlwatch_meta_database] db
		on db.[database_name] = idx.[database_name] collate database_default
		and db.[database_create_date] = idx.[create_date]
		and db.sql_instance = @@SERVERNAME

	inner join [dbo].[sqlwatch_meta_table] mt
		on mt.sql_instance = db.sql_instance
		and mt.sqlwatch_database_id = db.sqlwatch_database_id
		and mt.table_name = idx.table_name collate database_default

	left join [dbo].[sqlwatch_config_exclude_database] ed
		on db.[database_name] like ed.[database_name_pattern]
		and ed.[snapshot_type_id] = 3 --missing index logger.

	where ed.[snapshot_type_id] is null

		) as source
	on	target.sql_instance = source.sql_instance
	and target.sqlwatch_database_id = source.sqlwatch_database_id
	and target.sqlwatch_table_id = source.sqlwatch_table_id
	and target.index_handle = source.index_handle
	and isnull(target.[equality_columns],'') = isnull(source.[equality_columns],'') collate database_default
	and isnull(target.[inequality_columns],'') = isnull(source.[inequality_columns],'') collate database_default
	and isnull(target.[included_columns],'') = isnull(source.[included_columns],'') collate database_default
	and isnull(target.[statement],'') = isnull(source.[statement],'') collate database_default

when not matched by source and target.sql_instance = @@SERVERNAME then
	update set [is_record_deleted] = 1

when matched then
	update set [date_last_seen] = getutcdate(),
		[is_record_deleted] = 0

when not matched by target then
	insert ([sql_instance], [sqlwatch_database_id], [sqlwatch_table_id],		[equality_columns] ,
		[inequality_columns] ,[included_columns] ,[statement] , [index_handle] , [date_created])
	values (source.[sql_instance], source.[sqlwatch_database_id], source.[sqlwatch_table_id], source.[equality_columns] ,
		source.[inequality_columns] ,source.[included_columns] ,source.[statement] , source.[index_handle] , source.[date_added]);

--when not matched by source and target.sql_instance = @@SERVERNAME then
--	update set [date_deleted] = getutcdate();
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_internal_add_job]
AS

SET XACT_ABORT ON;
BEGIN TRAN

	merge [dbo].[sqlwatch_meta_agent_job] as target
	using msdb.dbo.sysjobs as source
	on (    target.sql_instance = @@SERVERNAME
		and target.job_name = source.name collate database_default
		and target.job_create_date = source.date_created
		)
	when not matched by target  then
		insert (sql_instance, job_name, job_create_date)
		values (@@SERVERNAME, source.name, source.date_created)

	when matched then
		update set
			[date_last_seen] = GETUTCDATE()
			
	when not matched by source and target.sql_instance = @@SERVERNAME then
		update set
			[is_record_deleted] = 1			
	;

	merge [dbo].[sqlwatch_meta_agent_job_step] as target
	using (
		select sql_instance = @@SERVERNAME, mj.sqlwatch_job_id, ss.step_name
		from msdb.dbo.sysjobsteps ss
		inner join msdb.dbo.sysjobs sj
			on ss.job_id = sj.job_id
		inner join dbo.sqlwatch_meta_agent_job mj
			on mj.job_name = sj.name collate database_default
			and mj.job_create_date = sj.date_created
			and mj.sql_instance = @@SERVERNAME	
	) as source
	on (
			target.sql_instance = source.sql_instance
		and target.step_name = source.step_name collate database_default
		and target.sqlwatch_job_id = source.sqlwatch_job_id
	)

	when not matched by source and target.sql_instance = @@SERVERNAME then
		update set [is_record_deleted] = 1

	when not matched by target then 
		insert (sql_instance, sqlwatch_job_id, step_name)
		values (@@SERVERNAME, source.sqlwatch_job_id, source.step_name)

	when matched then
		update set
			[date_last_seen] = GETUTCDATE(),
			[is_record_deleted] = 0

	;

COMMIT TRAN
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_internal_add_master_file]
as


merge [dbo].[sqlwatch_meta_master_file] as target
using (
	select mdb.sqlwatch_database_id, 
	mf.[file_id], mf.[type], mf.[physical_name], [sql_instance]=@@SERVERNAME, [file_name] = mf.[name] 
	,cast (case
	when left (ltrim (mf.physical_name), 2) = '\\' 
			then left (ltrim (mf.physical_name), charindex ('\', ltrim (mf.physical_name), charindex ('\', ltrim (mf.physical_name), 3) + 1) - 1)
		when charindex ('\', ltrim(mf.physical_name), 3) > 0 
			then upper (left (ltrim (mf.physical_name), charindex ('\', ltrim (mf.physical_name), 3) - 1))
		else mf.physical_name
	end as varchar(255)) as logical_disk
	from sys.master_files mf
	inner join dbo.vw_sqlwatch_sys_databases db
		on db.database_id = mf.database_id
	inner join [dbo].[sqlwatch_meta_database] mdb
		on mdb.sql_instance = @@SERVERNAME
		and mdb.database_name = convert(nvarchar(128),db.name) collate database_default
		and mdb.database_create_date = case when db.name = 'tempdb' then '1970-01-01 00:00:00.000' else db.[create_date] end
	) as source
 on (
		source.file_id = target.file_id
	and source.[file_name] = target.[file_name] collate database_default
	and source.physical_name = target.file_physical_name collate database_default
	and	source.sql_instance = target.sql_instance
 )

--when not matched by source and target.sql_instance = @@SERVERNAME then
--	update set [is_record_deleted] = 1

when matched and datediff(hour,[date_last_seen],getutcdate()) >= 24 then
	update
		set [date_last_seen] = getutcdate()
			--[is_record_deleted] = 0

when not matched by target then
	insert ( [sqlwatch_database_id], [file_id], [file_type], [file_physical_name], [sql_instance], [file_name], [logical_disk],[date_last_seen] )
	values ( source.[sqlwatch_database_id], source.[file_id], source.[type], source.[physical_name], source.[sql_instance], source.[file_name], source.[logical_disk], getutcdate() );

--when not matched by source and target.sql_instance = @@SERVERNAME then 
--	update set deleted_when = GETUTCDATE();
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_internal_add_memory_clerk]
as

;merge [dbo].[sqlwatch_meta_memory_clerk] as target
using (
	select distinct 
		[clerk_name] = [type] 
	from sys.dm_os_memory_clerks s
	union all
	select [clerk_name] = 'OTHER'
	) as source
on target.[clerk_name] = source.[clerk_name] collate database_default
and target.[sql_instance] = @@SERVERNAME

--when matched then 
--	update set date_last_seen = getutcdate()

when not matched then
	insert ([sql_instance], [clerk_name])
	values (@@SERVERNAME, source.[clerk_name]);
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_internal_add_os_volume] (
	@volume_name nvarchar(255),
	@label nvarchar(255),
	@file_system nvarchar(255),
	@block_size int
	)
as

merge [dbo].[sqlwatch_meta_os_volume] as target
using (
	select	volume_name = @volume_name,
			[label] = @label,
			[file_system] = @file_system,
			[volume_block_size_bytes] = @block_size,
			[sql_instance] = @@SERVERNAME
		) as source
on target.[volume_name] = source.[volume_name]
and target.[sql_instance] = source.[sql_instance]

-- #140
--when not matched by source and target.sql_instance = @@SERVERNAME then
--	update set [is_record_deleted] = 1

when matched then 
	update set [label] = source.[label],
		[file_system] = source.[file_system],
		[volume_block_size_bytes] = source.[volume_block_size_bytes],
		[date_updated] = case when 		
									target.[label] <> source.[label]
								or	target.[file_system] <> source.[file_system]
								or	target.[volume_block_size_bytes] <> source.[volume_block_size_bytes]
								then GETUTCDATE() else [date_updated] end,
		[date_last_seen] = GETUTCDATE(),
		[is_record_deleted] = 0

when not matched by target then
	insert ([sql_instance], [volume_name], [label], [file_system], [volume_block_size_bytes], [date_created], [date_last_seen])
	values (source.[sql_instance], source.[volume_name], source.[label], source.[file_system], source.[volume_block_size_bytes], GETUTCDATE(), GETUTCDATE());
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_internal_add_performance_counter]
as

create table #t (
	[sql_instance] varchar(32) not null,
	[object_name] nvarchar(128) not null,
	[counter_name] nvarchar(128) not null,
	[cntr_type] int null,
	[is_sql_counter] bit not null, 

	constraint pk_tmp_t 
	primary key clustered ([sql_instance],[object_name], [counter_name])
)


insert into #t with (tablock) ([sql_instance], [object_name], [counter_name], [cntr_type], [is_sql_counter]) 
select distinct 
		[sql_instance] = @@SERVERNAME
	, [object_name] = rtrim(pc.[object_name])
	, [counter_name] = rtrim(pc.[counter_name])
	, [cntr_type] = pc.[cntr_type]
	, [is_sql_counter] = 1
from sys.dm_os_performance_counters pc
		
union all
		
select 
	[sql_instance] = @@SERVERNAME
	, [object_name] = 'Win32_PerfFormattedData_PerfOS_Processor'
	, [counter_name] = 'Processor Time %'
	, [cntr_type] = 65792
	, [is_sql_counter] = 1 --this is faked from ring buffer. Perhaps we should change it now as we could get genuine counter from WMI via CLR


-- get non SQL counters via CLR if enabled:
if dbo.ufn_sqlwatch_get_clr_collector_status() = 1
	begin
		create table #c (
			object_name nvarchar(128),
			counter_name nvarchar(128),
			instance_name nvarchar(128)
		)

		insert into #c
		exec sp_executesql '
		select distinct *
		from dbo.ReadPerformanceCounterCategories()
		'

		create unique clustered index idx_tmp_c on #c ([object_name], [counter_name], instance_name)
		
		insert into #t with (tablock) ([sql_instance], [object_name], [counter_name], [cntr_type], [is_sql_counter]) 
		select distinct 
			  [sql_instance] = @@SERVERNAME
			, [object_name] = rtrim(pc1.[object_name])
			, [counter_name] = rtrim(pc1.[counter_name])
			, [cntr_type] = -1 --pc1.[cntr_type]
			, [is_sql_counter] = 0
		from #c pc1

		inner join dbo.[sqlwatch_config_performance_counters] sc with (nolock)
			on pc1.[object_name] like '%' + sc.[object_name] collate database_default
			and pc1.counter_name = sc.counter_name collate database_default
			and (
				pc1.instance_name = sc.instance_name collate database_default
				or	(
					sc.instance_name = '<* !_Total>' collate database_default
					and pc1.instance_name <> '_Total' collate database_default
					)
				)

		where sc.collect = 1
		--only non SQL Server Counters
		and pc1.[object_name] not like 'SQLServer%'
		and pc1.[object_name] not like 'MSSQL$%'

	end

;merge [dbo].[sqlwatch_meta_performance_counter] as target
using #t as source
	on target.sql_instance = source.sql_instance collate database_default
	and target.object_name = source.object_name collate database_default
	and target.counter_name = source.counter_name collate database_default

when matched and target.[is_sql_counter] is null then 
	update 
		set is_sql_counter = source.[is_sql_counter]

when not matched then
	insert ([sql_instance],[object_name],[counter_name],[cntr_type],[is_sql_counter])
	values (source.[sql_instance],source.[object_name],source.[counter_name],source.[cntr_type],source.[is_sql_counter]);


if dbo.ufn_sqlwatch_get_clr_collector_status() = 1
	begin
		---- while we're here, build distinct counter instances... this is currently only used to feed into the CLR function.
		---- in the future it will be used for all counters to reduce size of the counters logger
		;merge [dbo].[sqlwatch_meta_performance_counter_instance] as target
		using (
			select distinct 
				performance_counter_id, instance_name, mpc.sql_instance
			from #c c
			inner join [dbo].[sqlwatch_meta_performance_counter] mpc
			on mpc.sql_instance = @@SERVERNAME
			and mpc.object_name = c.object_name
			and mpc.counter_name = c.counter_name
			) as source
		on target.performance_counter_id = source.performance_counter_id
		and target.sql_instance = source.sql_instance
		and target.instance_name = source.instance_name

		when not matched then
			insert (performance_counter_id, instance_name, [sql_instance], [date_updated])
			values (source.performance_counter_id, source.instance_name, source.[sql_instance], getutcdate());
	end
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_internal_add_procedure]
as

begin
	set nocount on;
	set xact_abort on;

	declare @sql_instance varchar(32) = [dbo].[ufn_sqlwatch_get_servername]();

	merge [dbo].[sqlwatch_meta_procedure] as target
	using (

		-- whilst I could use sys.procedures to get a list of procedures in each database, I would have to loop through databases
		-- I am happy to just get procedures that have stats as otherwise there would be nothing to monitor anyway
		select
			distinct [procedure_name]=object_schema_name(ps.object_id, ps.database_id) + '.' + object_name(ps.object_id, ps.database_id),
			sd.sqlwatch_database_id,
			[procedure_type] = 'P',
			sql_instance = @sql_instance
		from sys.dm_exec_procedure_stats ps
		inner join dbo.vw_sqlwatch_sys_databases d
			on d.database_id = ps.database_id
		inner join dbo.sqlwatch_meta_database sd
			on sd.database_name = d.name collate database_default
			and sd.database_create_date = d.create_date
		where ps.type = 'P'

		union all

		--every statement executed in sql server goes through the optimiser and gets an execution plan.
		--from that point of view, stored procedures are just sql queries saved in sql server.
		--to make normalisation simpler, we are going to create a dummy procedure that will "hold" ad-hoc queries.
		select [procedure_name] = 'Ad-Hoc Query 3FBE6AA6'
			,  sqlwatch_database_id
			,  [procedure_type] = 'A' --also a made up type to make sure we keep the separate
			,  sql_instance = @sql_instance
		from dbo.sqlwatch_meta_database d
		where d.sql_instance = @sql_instance

	) as source
	on target.sql_instance = source.sql_instance
	and target.sqlwatch_database_id = source.sqlwatch_database_id
	and target.[procedure_name] = source.[procedure_name] collate database_default

	when matched and datediff(hour,[date_last_seen],getutcdate()) > 24 then
		update set [date_last_seen] = getutcdate()

	when not matched then 
		insert ([sql_instance],[sqlwatch_database_id],[procedure_name],[procedure_type],[date_first_seen],[date_last_seen])
		values (source.[sql_instance],source.[sqlwatch_database_id],source.[procedure_name],source.[procedure_type],getutcdate(),getutcdate());

end
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_internal_add_system_configuration]
as

merge [dbo].[sqlwatch_meta_system_configuration] as target
using (
		select [sql_instance]
		     , [configuration_id]
			 , [name]
			 , [value]
			 , [value_in_use]
			 , [description]
		from dbo.vw_sqlwatch_sys_configurations
		) as source
on target.configuration_id = source.configuration_id
and target.[sql_instance] = source.[sql_instance]

when matched 
	and (
			target.[value] <> source.[value] 
		or	target.[value_in_use] <> source.[value_in_use]
		)
	then 
	update set [value] = source.[value],
		[value_in_use] = source.[value_in_use]

when not matched by target then
	insert ([sql_instance], [configuration_id], [name], [description], [value], [value_in_use], [date_created])
	values (source.[sql_instance], source.[configuration_id], source.[name], source.[description], source.[value], source.[value_in_use], GETUTCDATE());
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_internal_add_table] (
	@databases varchar(max) = '-tempdb'
)
as

set nocount on;

if @databases = ''
	begin
		set @databases = '-tempdb'
	end

create table ##98308FFC2C634BF98B347EECB98E3490 (
	[TABLE_CATALOG] [nvarchar](128) NOT NULL,
	[TABLE_TYPE] [varchar](10) NULL,
	[table_name] nvarchar(512) NOT NULL
	constraint PK_TMP_98308FFC2C634BF98B347EECB98E3490 primary key clustered (
		[TABLE_CATALOG], [table_name]
		)
)

--https://github.com/marcingminski/sqlwatch/issues/176
declare @sqlwatch_sys_databases table (
	[name] [sysname] NOT NULL,
	[create_date] [datetime] NOT NULL,
	UNIQUE ([name],[create_date])
)

insert into @sqlwatch_sys_databases
SELECT name, create_date
FROM [dbo].[vw_sqlwatch_sys_databases]

exec [dbo].[usp_sqlwatch_internal_foreachdb] @command = '
USE [?]
insert into ##98308FFC2C634BF98B347EECB98E3490 ([TABLE_CATALOG],[table_name],[TABLE_TYPE])
SELECT [TABLE_CATALOG],[table_name] = [TABLE_SCHEMA] + ''.'' + [TABLE_NAME],[TABLE_TYPE] 
from INFORMATION_SCHEMA.TABLES with (nolock)
WHERE''?'' <> ''tempdb''', @databases = @databases, @calling_proc_id = @@PROCID

/* when collecting tables we only consider name as a primary key. 
   when table is dropped and recreated with the same name, we are treating it as the same table.
   this behaviour is different to how we handle database. Quite often there are ETL processes that drop
   and re-create tabe every nigth for example */
merge [dbo].[sqlwatch_meta_table] as target
using (
	select [t].[TABLE_CATALOG], [t].[table_name], [t].[TABLE_TYPE], mdb.sqlwatch_database_id, mtb.sqlwatch_table_id
	from ##98308FFC2C634BF98B347EECB98E3490 t
	inner join @sqlwatch_sys_databases dbs
		on dbs.name = t.TABLE_CATALOG collate database_default
	inner join [dbo].[sqlwatch_meta_database] mdb
		on mdb.database_name = dbs.name collate database_default
		and mdb.database_create_date = dbs.create_date
		and mdb.sql_instance = @@SERVERNAME
	left join [dbo].[sqlwatch_meta_table] mtb
		on mtb.sql_instance = mdb.sql_instance
		and mtb.sqlwatch_database_id = mdb.sqlwatch_database_id
		and mtb.table_name = t.table_name collate database_default
	) as source
 on		target.sql_instance = @@SERVERNAME
 and	target.[table_name] = source.[table_name] collate database_default
 and	target.[table_type] = source.[table_type] collate database_default
 and	target.[sqlwatch_database_id] = source.[sqlwatch_database_id]

 		
/* we dont need is record deleted field as its not always possible to tell.
   we're using date last seen to handle this status */
--when not matched by source and target.sql_instance = @@SERVERNAME then
--	update set [is_record_deleted] = 1

 when matched 
	and target.sql_instance = @@SERVERNAME 
	-- The SqlWatchImport relies on date_last_seen to speed up imports
	-- and the field is only used for the retention purposes.
	-- We will only update it if its passsed 24h.
	-- New tables will be picked up immediately.

	-- On instances with large number of databases and tables,
	-- this procedures should only run once a day.
	and datediff(hour,[date_last_seen],GETUTCDATE()) >= 24
	then update set [date_last_seen] = GETUTCDATE()
		--,[is_record_deleted] = 0

/* a new database and/or table could have been added since last collection.
	in which case we have not got id yet, it will be picked up with the next cycle */
 when not matched by target and source.[sqlwatch_database_id] is not null then
	insert ([sql_instance],[sqlwatch_database_id],[table_name],[table_type],[date_first_seen],[date_last_seen])
	values (@@SERVERNAME,source.[sqlwatch_database_id],source.[table_name],source.[table_type],GETUTCDATE(),GETUTCDATE());

 --when matched and [date_deleted] is not null and target.sql_instance = @@SERVERNAME then
	--update set [date_deleted] = null;

if object_id('tempdb..##98308FFC2C634BF98B347EECB98E3490') is not null
	drop table ##98308FFC2C634BF98B347EECB98E3490
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_internal_add_wait_type]
AS


declare @sql_instance varchar(32) = dbo.ufn_sqlwatch_get_servername();

;merge [dbo].[sqlwatch_meta_wait_stats] as target
using (
	select ws.*, [is_excluded] = case when ews.wait_type is not null then 1 else 0 end 
	from sys.dm_os_wait_stats ws
	left join [dbo].[sqlwatch_config_exclude_wait_stats] ews
		on ews.[wait_type] = ws.wait_type collate database_default
	) as source
	on target.[wait_type] = source.[wait_type] collate database_default
	and target.[sql_instance] = @sql_instance 
		
when matched then 
	update set [is_excluded] = source.[is_excluded]

when not matched then 
	insert ([sql_instance], [wait_type], [is_excluded])
	values (@sql_instance, source.[wait_type], source.[is_excluded]);
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_internal_broker_diagnostics]
as

select [info] = 'A simple proc to help you see whats going on in the broker and help diagnose problems'

--get queue and service status:
select 
	  [service] = s.name 
	, [queue] = q.name
	, q.is_receive_enabled
	, q.is_activation_enabled
	, q.activation_procedure
	, info = 'is_receive_enabled and is_activation_enabled should both return 1 which means they are active and listening for messages'
from   sys.services s
	inner join sys.service_queues q
		on s.service_queue_id = q.object_id
where q.is_ms_shipped = 0

-- get items in the queue as of now -- handy if you have stuck errors
select *, cast(message_body as xml) 
from [dbo].[sqlwatch_exec];

select [info] = 'This should return a row for each initiator (is_initiator=1) STARTED_OUTBOUND status and all the other rows shuold have status CLOSED (as these have finished and are waiting for SQL to clean them up)
There may be messages that are currently being processed or are awaiting processing but in general they will drop off soon into CLOSED'
select * from sys.conversation_endpoints
where far_service = 'sqlwatch_exec'
order by lifetime desc
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_internal_create_agent_job]
	@print_WTS_command bit,
	@job_owner sysname = null
as
set nocount on;

declare @job_description nvarchar(255) = 'https://sqlwatch.io',
		@job_category nvarchar(255) = 'Data Collector',
		@database_name sysname = 'SQLWATCH',
		@command nvarchar(4000),
		@wts_command varchar(max) = '',
		@sql varchar(max),
		@enabled tinyint = 1,
		@server nvarchar(255)

set @server = @@SERVERNAME

/* fixed job ownership originally submmited by SvenLowry
	https://github.com/marcingminski/sqlwatch/pull/101/commits/8772e56df3aa80849b1dac85405641feb6112e5c 
	
	if no user specified job owner passed, we are going to assume sa, or renamed sa based on the sid. */

if @job_owner is null
	begin
		set @job_owner = (select [name] from syslogins where [sid] = 0x01)
	end


--adding database name to the job name if not standard SQLWATCH for better clarity and to be able to deploy multiple SQLWATCH databases and corresponding jobs.
if 'SQLWATCH' not in ('sqlwatch','SQLWATCH')
	begin
		update ##sqlwatch_jobs set job_name = replace(job_name,'SQLWATCH-','SQLWATCH-[' +  'SQLWATCH' + ']-')
		update ##sqlwatch_steps set job_name = replace(job_name,'SQLWATCH-','SQLWATCH-[' +  'SQLWATCH' + ']-')
	end

/* create job and steps */
select @sql = replace(replace(convert(nvarchar(max),(select ' if (select name from msdb.dbo.sysjobs where name = ''' + job_name + ''') is null 
	begin
		exec msdb.dbo.sp_add_job @job_name=N''' + job_name + ''',  @owner_login_name=N''' + @job_owner + ''', @category_name=N''' + @job_category + ''', @enabled=' + convert(char(1),job_enabled) + ',@description=''' + @job_description + ''';
		exec msdb.dbo.sp_add_jobserver @job_name=N''' + job_name + ''', @server_name = ''' + @server + ''';
		' + (select 
				' exec msdb.dbo.sp_add_jobstep @job_name=N''' + job_name + ''', @step_name=N''' + step_name + ''',@step_id= ' + convert(varchar(10),step_id) + ',@subsystem=N''' + step_subsystem + ''',@command=''' + replace(step_command,'''','''''') + ''',@on_success_action=' + case when ROW_NUMBER() over (partition by job_name order by step_id desc) = 1 then '1' else '3' end +', @on_fail_action=' + case when ROW_NUMBER() over (partition by job_name order by step_id desc) = 1 then '2' else '3' end + ', @database_name=''' + @database_name + ''''
			 from ##sqlwatch_steps 
			 where ##sqlwatch_steps.job_name = ##sqlwatch_jobs.job_name 
			 order by step_id asc
			 for xml path ('')) + '
		exec msdb.dbo.sp_update_job @job_name=N''' + job_name + ''', @start_step_id=1
		exec msdb.dbo.sp_add_jobschedule @job_name=N''' + job_name + ''', @name=N''' + job_name + ''', @enabled=1,@freq_type=' + convert(varchar(10),freq_type) + ',@freq_interval=' + convert(varchar(10),freq_interval) + ',@freq_subday_type=' + convert(varchar(10),freq_subday_type) + ',@freq_subday_interval=' + convert(varchar(10),freq_subday_interval) + ',@freq_relative_interval=' + convert(varchar(10),freq_relative_interval) + ',@freq_recurrence_factor=' + convert(varchar(10),freq_recurrence_factor) + ',@active_start_date=' + convert(varchar(10),active_start_date) + ',@active_end_date=' + convert(varchar(10),active_end_date) + ',@active_start_time=' + convert(varchar(10),active_start_time) + ',@active_end_time=' + convert(varchar(10),active_end_time) + ';
		Print ''Job ''''' + job_name + ''''' created.''
	end
else
	begin
		Print ''Job ''''' + job_name + ''''' not created because it already exists.''
	end;
	' + case 
		when /* trends must run once an hour */ job_name not like '%INTERNAL-TRENDS' 
		and /* job has not run yet */ h.run_status is null 
		and /* only if its the first deployment */ v.deployment_count <= 1 
		and job_enabled = 1 then 'exec [dbo].[usp_sqlwatch_internal_run_job] @fail_on_error = 0, @job_name = ''' + job_name + '''' else '' end + '
	'
	from ##sqlwatch_jobs
	outer apply (
		select top 1 run_status 
		from msdb.dbo.sysjobhistory jh
		inner join msdb.dbo.sysjobs sj
			on sj.job_id = jh.job_id
		where sj.name = job_name 
		and step_id = 0 
		order by run_date desc, run_time desc
	) h
	outer apply (
		select count(*) as deployment_count
		from [dbo].[sqlwatch_app_version]
	) v
	order by job_id
	for xml path ('')
)),'&#x0D;',''),'&amp;#x0D;','')

exec (@sql)


WTS:
if @print_WTS_command = 1
	begin
		Print '

----------------------------------------------------------------------------------------------------------------------------------------
Generate PowerShell script to Create Windows Scheduled Task to execute SQLWATCH Collectors on the SQL Express edition
Only create windows tasks on servers that have no agent job, otheriwse double data collection will take place and fail due to PK violation.
The reason we use PowerShell instead of SchTasks is to be able to create multiple actions per task, same as multiple steps per job.
SchTasks does not support more than one /TR parameter.

https://docs.microsoft.com/en-us/powershell/module/scheduledtasks/new-scheduledtasktrigger
----------------------------------------------------------------------------------------------------------------------------------------
'	
/*	It would make sense to have the above in the same cursor but I do not want to change that now, it has been working fine for a long time.
	I will get around to it at some point.
*/

Print 'Fnding Binn path. Ignore any 22001 RegOpenKeyEx() errors below'
declare @val nvarchar(512)

if @val is null
	begin
		exec master.dbo.xp_instance_regread N'HKEY_LOCAL_MACHINE', N'SOFTWARE\\Microsoft\\Microsoft SQL Server\\100\\Tools\\ClientSetup\\', 'Path', @val OUTPUT
	end

if @val is null
	begin
		exec master.dbo.xp_instance_regread N'HKEY_LOCAL_MACHINE', N'SOFTWARE\\Microsoft\\Microsoft SQL Server\\110\\Tools\\ClientSetup\\', 'Path', @val OUTPUT
	end

if @val is null
	begin
		exec master.dbo.xp_instance_regread N'HKEY_LOCAL_MACHINE', N'SOFTWARE\\Microsoft\\Microsoft SQL Server\\120\\Tools\\ClientSetup\\', 'Path', @val OUTPUT
	end

if @val is null
	begin
		exec master.dbo.xp_instance_regread N'HKEY_LOCAL_MACHINE', N'SOFTWARE\\Microsoft\\Microsoft SQL Server\\130\\Tools\\ClientSetup\\', 'Path', @val OUTPUT
	end

if @val is null
	begin
		exec master.dbo.xp_instance_regread N'HKEY_LOCAL_MACHINE', N'SOFTWARE\\Microsoft\\Microsoft SQL Server\\140\\Tools\\ClientSetup\\', 'Path', @val OUTPUT
	end

if @val is null
	begin
		exec master.dbo.xp_instance_regread N'HKEY_LOCAL_MACHINE', N'SOFTWARE\\Microsoft\\Microsoft SQL Server\\150\\Tools\\ClientSetup\\', 'Path', @val OUTPUT
	end

Print '

----------------------------------------------------------------------------------------------------------------------------------------
Copy the below into PowerShell ISE and execute
----------------------------------------------------------------------------------------------------------------------------------------'

Print '<# ----------------------------------------------------------------------------------------------------------------------------------------
Scheduled tasks can only accept 261 characters long commands which is not enough for our PowerShell commands.
We are going to dump these into ps1 files and execute these files from the scheduler. Default location will be:
C:\SQLWATCHPS so feel free to change this before executing this script 
---------------------------------------------------------------------------------------------------------------------------------------- #>

$PSPath = "C:\SQLWATCHPS"

<# ----------------------------------------------------------------------------------------------------------------------------------------
Windows Task scheduler normally only runs when the user is logged in. To make it run all the time we have to give it an account under which it will run.
Whilst it is technically possible to run it as SYSTEM, as long as SYSTEM has access to the SQL Server, it is quite insecure. 
Best practice is to create dedicated Windows user i.e. SQLWATCHUSER and *** GRANT BATCH LOGON RIGHTS *** and required access to the SQL Server. 
You can read more about task principals here: https://docs.microsoft.com/en-us/powershell/module/scheduledtasks/new-scheduledtaskprincipal

In your enviroment you will want something like:
$User = "sqlwatch"
$Password = "UserPassword"
$LogonType = "Password"

However, to make this example and scripting easier, we are going to asume LOCALSERVICE. 
Note that the account will need access to the SQL Server and the SQLWATCH database according to the access requirements.
---------------------------------------------------------------------------------------------------------------------------------------- #>

$User = "LOCALSERVICE" #Change in your environemnt to a dedicated user
$LogonType = "ServiceAccount"

<# ---------------------------------------------------------------------------------------------------------------------------------------- #>

If (!(Test-Path $PSPath)) {
    New-Item $PSPath -ItemType Directory
   }
'

declare @job_name sysname,
		@step_name sysname,
		@step_command varchar(max),
		@step_subsystem sysname,
		@step_id int,
		@start_time int,
		@string_time varchar(10),
		@freq_type int,
		@freq_interval int,
		@freq_subday_type int,
		@freq_subday_interval int,
		@task_name sysname

declare cur_jobs cursor for
select distinct task_name = j.job_name, j.job_name, active_start_time, freq_type, freq_interval, freq_subday_type, freq_subday_interval, job_enabled
from ##sqlwatch_jobs j

open cur_jobs

fetch next from cur_jobs into @task_name, @job_name, @start_time, @freq_type, @freq_interval, @freq_subday_type, @freq_subday_interval, @enabled

while @@FETCH_STATUS = 0
	begin
		Print '
## ' + @job_name
		set @command = ''
		set @command = '$actions=@()'
		set @string_time = right('000000' + convert(varchar(6),@start_time), 6)

		declare cur_job_steps cursor
		for select step_name, step_command, step_subsystem, step_id
		from ##sqlwatch_steps
		where job_name = @job_name
		order by step_id

		open cur_job_steps
		fetch next from cur_job_steps 
		into @step_name, @step_command, @step_subsystem, @step_id

		while @@FETCH_STATUS = 0
			begin

				if @step_subsystem = 'TSQL'
					begin
						set @command = @command + char(10) + '$actions+=New-ScheduledTaskAction –Execute ''' + @val + 'osql.exe '' -Argument ''-E -S "' + @server + '" -d "' + @database_name + '" -Q "' + @step_command + ';"' + ''''
					end

				if @step_subsystem = 'PowerShell'
					begin
						set @command = @command + char(10) + 'If (!(Test-Path "$PSPath\' + @job_name + '")) {
    New-Item "$PSPath\' + @job_name + '" -ItemType Directory
   }'
						set @command = @command + char(10) + '@''
' + @step_command + '
''@ | Out-File "$PSPath\' + @job_name + '\' + @step_name +'.ps1"'
						set @command = @command + char(10) + '$actions+=New-ScheduledTaskAction –Execute ''PowerShell.exe'' -Argument ' + '$' + '(''-file "''+' + ' $' + '( $PSPath ) + ''\' + @job_name + '\' + @step_name +'.ps1"'' )'
					end

				fetch next from cur_job_steps 
				into @step_name, @step_command, @step_subsystem, @step_id
			end

		set @string_time = left(@string_time, 2) + ':' + right(left(@string_time, 4), 2) + ':' + right(left(@string_time, 8), 2)

		set @command = @command + char(10) + '$trigger=New-ScheduledTaskTrigger -' + case @freq_type
			when 1 then 'Once'
			when 4 then 'Daily'
			when 8 then 'Weekly'
			when 16 then 'Monthly'
			end + ' -At ''' + convert(varchar(10),@string_time) + ''''

		set @command = @command + char(10) + '$principal=New-ScheduledTaskPrincipal -UserId $User -LogonType $LogonType'
		set @command = @command + char(10) + '$task=New-ScheduledTask -Action $actions -Trigger $trigger -Principal $principal'
		set @command = @command + char(10) + 'if ( $Password -ne "" -and $Password -ne $null ) {
Register-ScheduledTask "' + @task_name + '" -InputObject $task -User $User -Password $Password
} else {
Register-ScheduledTask "' + @task_name + '" -InputObject $task -User $User
}'
		
		/*	The amount of time between each restart of the task. The format for this string is PDTHMS (for example, "PT5M" is 5 minutes, "PT1H" is 1 hour, and "PT20M" is 20 minutes). 
			The maximum time allowed is 31 days, and the minimum time allowed is 1 minute.	*/
		set @command = @command + char(10) + '$task = Get-ScheduledTask -TaskName "' + @task_name + '"'

		/* It's all fun and games until you realise you have to translate SQL frequency types and intervals into the repetition format. 
			Surely these two teams at MS could talk...I am only going to support frequencies and types used in SQLWATCH otherwise it's quite a task. 
			https://docs.microsoft.com/en-us/windows/win32/taskschd/repetitionpattern-interval?redirectedfrom=MSDN
			https://docs.microsoft.com/en-us/sql/relational-databases/system-stored-procedures/sp-add-schedule-transact-sql		
			*/
		set @command = @command + char(10) + '$task.Triggers.repetition.Duration = "P' + case @freq_type
			when 4 then + convert(varchar(10),@freq_interval)
			else '' end + 'D"'

		set @command = @command + char(10) + '$task.Triggers.repetition.Interval = "PT' + case @freq_subday_type
				when 2 then '1M' --Task scheduler does not support seconds, most frequent it can run is 1 minute.
				when 4 then convert(varchar(10),@freq_subday_interval) + 'M'
				when 8 then convert(varchar(10),@freq_subday_interval) + 'H'
				else '' end + '"'		
		set @command = @command + char(10) + 'if ( $Password -ne "" -and $Password -ne $null ) {
$task | Set-ScheduledTask -User $User -Password $Password
} else {
$task | Set-ScheduledTask -User $User
}'

		if @enabled = 0
			begin
				set @command = @command + char(10) + 'Disable-ScheduledTask -TaskName "' + @task_name + '"'
			end
		Print @command 
		close cur_job_steps
		deallocate cur_job_steps
		fetch next from cur_jobs into @task_name, @job_name, @start_time, @freq_type, @freq_interval, @freq_subday_type, @freq_subday_interval, @enabled
	end

close cur_jobs
deallocate cur_jobs


if object_id('tempdb..##sqlwatch_steps') is not null
	drop table ##sqlwatch_steps

if object_id('tempdb..##sqlwatch_jobs') is not null
	drop table ##sqlwatch_jobs

end
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_internal_exec_activated]
as
begin
	set nocount on;

    declare @conversation_handle    uniqueidentifier,
            @message_type_name      nvarchar(128),
            @message_body           xml,
            @error_number           int,
            @error_message          nvarchar(max),
            @this_procedure_name    nvarchar(128),
            @sql                    nvarchar(max),
            @sql_params             nvarchar(max),
            @conversation_group_id  uniqueidentifier,
            @procedure_name         nvarchar(128),
            @timer                  smallint,
            @timestart              datetime2(7),
            @process_message        varchar(4000);

        begin try;

            set @this_procedure_name = OBJECT_NAME(@@PROCID);

            -- get items from our queue            
            receive top(1)
                  @conversation_handle = [conversation_handle]
                , @message_type_name = [message_type_name]
                , @message_body = cast([message_body] as xml)
		        , @conversation_group_id = conversation_group_id
                from dbo.sqlwatch_exec;
            
            -- if procedure is in the message body, it means we're running async execution rather than timer
            set @procedure_name = @message_body.value('(//procedure/name)[1]', 'nvarchar(128)');

            if @conversation_handle is not null
                begin

                    begin try

                        set @process_message = null;

                        if  @message_type_name = N'DEFAULT' and @procedure_name is not null
                            begin
                                set @timestart = SYSDATETIME();

                                exec @procedure_name;

                                set @process_message = 'Message Type: ' + convert(varchar(4000),@message_type_name) + '; Procedure: ' + @procedure_name + '; Time Taken: ' + convert(varchar(100),datediff(ms,@timestart,SYSDATETIME())) + 'ms'
                            end

                        else if @message_type_name = N'http://schemas.microsoft.com/SQL/ServiceBroker/DialogTimer'
                            begin
                                
                                /* this could be a generic worker that we pass group id into and it works out what to run based on some meta data.
                                   for now however this will be hardcoded in batches 
                                   
                                   This code will execute procedures synchronously as sometimes dependencies are required.
                                   For example, we first want to collect database before we collect tables
                                        exec dbo.usp_sqlwatch_internal_add_database;
                                        exec dbo.usp_sqlwatch_internal_add_table;

                                   But we can also enqueue procedure to run asynchronously using:
                                   exec [dbo].[usp_sqlwatch_internal_exec_activated_async] @procedure_name = 'dbo.usp_sqlwatch_logger_xes_blockers'

                                 */


                                -- 5 seconds batch
			                    if @conversation_group_id = 'B273076A-5D10-4527-909F-955707905890'
                                    begin                                        
                                        set @timer = 5
                                        begin conversation timer (@conversation_handle) timeout = @timer;
                                        
                                        set @timestart = SYSDATETIME();

                                        begin try

                                            exec dbo.usp_sqlwatch_logger_performance;
                                            exec dbo.[usp_sqlwatch_logger_requests_and_sessions];
                                        
                                        end try
                                        begin catch
                                            if @@TRANCOUNT > 0
                                                rollback transaction

                                            set @process_message = 'Activated procedure failed.'
                                            exec [dbo].[usp_sqlwatch_internal_log]
					                            @proc_id = @@PROCID,
					                            @process_stage = '130C078A-2AB2-4F07-AA5B-EA810388A553',
					                            @process_message = @process_message,
					                            @process_message_type = 'ERROR'

                                            Print @process_message + ' Please check application log for details.'
                                        end catch

                                        -- run async procedures now as they have their own error handler
                                        exec [dbo].[usp_sqlwatch_internal_exec_activated_async] @procedure_name = 'dbo.usp_sqlwatch_logger_xes_blockers';

                                        set @process_message = 'Message Type: ' + convert(varchar(4000),@message_type_name) + '; Timer: ' + convert(varchar(5),@timer) + '; Time Taken: ' + convert(varchar(100),datediff(ms,@timestart,SYSDATETIME()))  + 'ms'
                                    
                                    end

                                -- 1 minute batch
			                    if @conversation_group_id = 'A2719CB0-D529-46D6-8EFE-44B44676B54B'
                                    begin
                                        set @timer = 60;
                                        begin conversation timer (@conversation_handle) timeout = @timer;

                                        set @timestart = SYSDATETIME();

                                        -- execute async via broker:
                                        exec [dbo].[usp_sqlwatch_internal_exec_activated_async] @procedure_name = 'dbo.usp_sqlwatch_internal_process_checks';
                                        exec [dbo].[usp_sqlwatch_internal_exec_activated_async] @procedure_name = 'dbo.usp_sqlwatch_logger_hadr_database_replica_states';

                                        begin try
                                            -- execute in sequence:
                                            exec dbo.usp_sqlwatch_logger_xes_waits
                                            exec dbo.usp_sqlwatch_logger_xes_diagnostics
                                            exec dbo.usp_sqlwatch_logger_xes_long_queries
                                           -- exec dbo.usp_sqlwatch_logger_xes_query_problems
                                        end try
                                        begin catch
                                            if @@TRANCOUNT > 0
                                                rollback transaction

                                            set @process_message = 'Activated procedure failed.'
                                            exec [dbo].[usp_sqlwatch_internal_log]
					                            @proc_id = @@PROCID,
					                            @process_stage = '34D2EFC9-5128-4117-AD11-2849828CFF6E',
					                            @process_message = @process_message,
					                            @process_message_type = 'ERROR'

                                            Print @process_message + ' Please check application log for details.'
                                        end catch

                                        set @process_message = 'Message Type: ' + convert(varchar(4000),@message_type_name) + '; Timer: ' + convert(varchar(5),@timer) + '; Time Taken: ' + convert(varchar(100),datediff(ms,@timestart,SYSDATETIME()))  + 'ms'

                                    end

                                -- 10 minute batch
			                    if @conversation_group_id = 'F65F11A7-25CF-4A4D-8A4F-C75B03FE083F'
                                    begin
                                        set @timer = 600;
                                        begin conversation timer (@conversation_handle) timeout = @timer;

                                        set @timestart = SYSDATETIME();

                                        begin try
                                            exec dbo.usp_sqlwatch_logger_agent_job_history
                                            exec dbo.usp_sqlwatch_logger_procedure_stats;
                                        end try
                                        begin catch
                                            if @@TRANCOUNT > 0
                                                rollback transaction

                                            set @process_message = 'Activated procedure failed.'
                                            exec [dbo].[usp_sqlwatch_internal_log]
					                            @proc_id = @@PROCID,
					                            @process_stage = '0C1A3576-0B40-4871-8D4E-7490F5B91910',
					                            @process_message = @process_message,
					                            @process_message_type = 'ERROR'

                                            Print @process_message + ' Please check application log for details.'
                                        end catch

                                        set @process_message = 'Message Type: ' + convert(varchar(4000),@message_type_name) + '; Timer: ' + convert(varchar(5),@timer) + '; Time Taken: ' + convert(varchar(100),datediff(ms,@timestart,SYSDATETIME()))  + 'ms'

                                    end

                                -- 1 hour batch
			                    if @conversation_group_id = 'E623DC39-A79D-4F51-AAAD-CF6A910DD72A'
                                    begin
                                        set @timer = 3600;
                                        begin conversation timer (@conversation_handle) timeout = @timer;

                                        set @timestart = SYSDATETIME();

                                        begin try

                                            --execute in sequence:
                                            exec dbo.usp_sqlwatch_internal_add_database;
                                            exec dbo.usp_sqlwatch_internal_add_master_file;
                                            exec dbo.usp_sqlwatch_internal_add_table;
                                            exec dbo.usp_sqlwatch_internal_add_job;
                                            exec dbo.usp_sqlwatch_internal_add_performance_counter;
                                            exec dbo.usp_sqlwatch_internal_add_memory_clerk;
                                            exec dbo.usp_sqlwatch_internal_add_wait_type;
                                            exec dbo.usp_sqlwatch_internal_add_index;
                                            exec dbo.usp_sqlwatch_internal_add_procedure;

                                            --exec dbo.usp_sqlwatch_logger_disk_utilisation;

                                            --trends:
                                            exec dbo.usp_sqlwatch_trend_perf_os_performance_counters @interval_minutes = 1, @valid_days = 7;
                                            exec dbo.usp_sqlwatch_trend_perf_os_performance_counters @interval_minutes = 5, @valid_days = 90;
                                            exec dbo.usp_sqlwatch_trend_perf_os_performance_counters @interval_minutes = 60, @valid_days = 720;
                                        end try
                                        begin catch
                                            if @@TRANCOUNT > 0
                                                rollback transaction

                                            set @process_message = 'Activated procedure failed.'
                                            exec [dbo].[usp_sqlwatch_internal_log]
					                            @proc_id = @@PROCID,
					                            @process_stage = '441D488C-5872-4602-99E0-9C8080041DE9',
					                            @process_message = @process_message,
					                            @process_message_type = 'ERROR'

                                            Print @process_message + ' Please check application log for details.'
                                        end catch

                                        --execute async via broker:
                                        exec [dbo].[usp_sqlwatch_internal_exec_activated_async] @procedure_name = 'dbo.usp_sqlwatch_internal_retention';
                                        exec [dbo].[usp_sqlwatch_internal_exec_activated_async] @procedure_name = 'dbo.usp_sqlwatch_internal_purge_deleted_items';
                                        exec [dbo].[usp_sqlwatch_internal_exec_activated_async] @procedure_name = 'dbo.usp_sqlwatch_internal_expand_checks';
                                        exec [dbo].[usp_sqlwatch_internal_exec_activated_async] @procedure_name = 'dbo.usp_sqlwatch_internal_add_index_missing';
                                        exec [dbo].[usp_sqlwatch_internal_exec_activated_async] @procedure_name = 'dbo.usp_sqlwatch_logger_errorlog';

                                        set @process_message = 'Message Type: ' + convert(varchar(4000),@message_type_name) + '; Timer: ' + convert(varchar(5),@timer) + '; Time Taken: ' + convert(varchar(100),datediff(ms,@timestart,SYSDATETIME()))  + 'ms'

                                    end     
                            end      
                    
				        if @process_message is not null
                            begin
                                exec [dbo].[usp_sqlwatch_internal_log]
					                @proc_id = @@PROCID,
					                @process_stage = '375C6590-D88D-4115-B8ED-2C0B6B6993D0',
					                @process_message = @process_message,
					                @process_message_type = 'INFO'
                            end

                    end try
                    begin catch
                        select  @error_number = ERROR_NUMBER(),
                                @error_message = ERROR_MESSAGE()
                            
                        if @@TRANCOUNT > 0
                            begin
                                rollback
                            end
                        end conversation @conversation_handle
                        raiserror(N'Error whilst executing SQLWATCH Procedure %s: %i: %s', 16, 10, @procedure_name, @error_number, @error_message);
                    end catch

                    if @message_type_name = N'http://schemas.microsoft.com/SQL/ServiceBroker/Error'
                        begin
                            -- we should get the error content from the broker here and output to the errorlog
                            select 
                                @error_message = @message_body.value ('(/Error/Description)[1]', 'nvarchar(4000)')
                            ,   @error_number = @message_body.value ('(/Error/Code)[1]', 'int')

                           --set @process_message = 'Message Type: ' + convert(varchar(4000),@message_type_name) + ';  (' + convert(varchar(100),@error_number) + ') ' + @error_message
                           --
                           --exec [dbo].[usp_sqlwatch_internal_log]
					       --    @proc_id = @@PROCID,
					       --    @process_stage = '17228F19-F167-48F2-AA3E-477516F64515',
					       --    @process_message = @process_message,
					       --    @process_message_type = 'ERROR'

                            print 'The converstaion ' + convert(varchar(max),@conversation_handle) + ' has returned an error (' + convert(varchar(10),@error_number) + ') ' + @error_message

                            end conversation @conversation_handle
                        end

                    if (
                            @message_type_name = N'http://schemas.microsoft.com/SQL/ServiceBroker/EndDialog'
                        or  @message_type_name = N'DEFAULT'
                        )
                        begin
                            end conversation @conversation_handle
                        end
                end
            else
                begin
                    if @@TRANCOUNT > 0
                        begin
                            rollback
                            end conversation @conversation_handle;
                        end
                    --raiserror(N'Variable @procedure_name in %s is null', 10, 10, @this_procedure_name);
                end
        end try
        begin catch
            select  @error_number = ERROR_NUMBER(),
                    @error_message = ERROR_MESSAGE()
                    
            if @@TRANCOUNT > 0
                begin
                    rollback;
                    end conversation @conversation_handle;
                end
            raiserror(N'Error whilst executing SQLWATCH Procedure %s: %i: %s', 16, 10, @this_procedure_name, @error_number, @error_message);
        end catch
end
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_internal_exec_activated_async]
    @procedure_name nvarchar(128)
as

    declare @conversation_handle uniqueidentifier,
            @xmlBody xml;

    set @xmlBody = (
            select @procedure_name as [name]
            for xml path('procedure')
            , type);

    begin dialog conversation @conversation_handle
        from service sqlwatch_exec
        to service N'sqlwatch_exec', N'current database'
        with encryption = off,
        lifetime = 60;

    send on conversation @conversation_handle (@xmlBody);
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_internal_expand_checks]
as
begin

	set nocount on ;

	declare @check_name varchar(255),
			@expand_by varchar(50),
			@check_template_id smallint,
			@sql_instance varchar(32);

	declare cur_expand_by_server cursor for
	select [sql_instance]
	from dbo.sqlwatch_config_sql_instance
	--only expand by all instances if set in the config, otherwise just expand by local instance
	where sql_instance = case when dbo.ufn_sqlwatch_get_config_value (19, null) = 1 
		then sql_instance 
		else dbo.ufn_sqlwatch_get_servername()
		end;

	open cur_expand_by_server

	fetch next from cur_expand_by_server
		into @sql_instance

	while @@FETCH_STATUS = 0
	begin

		declare cur_expand_check cursor LOCAL FAST_FORWARD for
		select check_name, expand_by, check_template_id
		from [dbo].[sqlwatch_config_check_template]
		where template_enabled = 1

		open cur_expand_check 
	
		fetch next from cur_expand_check 
			into @check_name, @expand_by , @check_template_id
	
		while @@FETCH_STATUS = 0 
		begin

			declare @checks table (	
				[check_template_id] smallint,
				[check_name] [nvarchar](255) NOT NULL,
				[check_description] [nvarchar](2048) NULL,
				[check_query] [nvarchar](max) NOT NULL,
				[check_frequency_minutes] [smallint] NULL,
				[check_threshold_warning] [varchar](100) NULL,
				[check_threshold_critical] [varchar](100) NOT NULL,
				[check_enabled] [bit] NOT NULL,
				[ignore_flapping] [bit] NOT NULL,
				[object_type] varchar(50),
				[object_name] nvarchar(128),
				[target_sql_instance] varchar(32),
				[use_baseline] bit
			)

			if @expand_by is null
				begin
					insert into @checks (
							[check_name],[check_description],[check_query],[check_frequency_minutes],[check_threshold_warning]
						   ,[check_threshold_critical],[check_enabled],[ignore_flapping],[check_template_id], [target_sql_instance]
						   ,[use_baseline]
					)

					select 
						[check_name]
					   ,[check_description]
					   ,[check_query]=replace(check_query,'{SQL_INSTANCE}',@sql_instance)
					   ,[check_frequency_minutes]
					   ,[check_threshold_warning]
					   ,[check_threshold_critical]
					   ,[check_enabled]
					   ,[ignore_flapping]
					   ,[check_template_id]
					   ,@sql_instance
					   ,[use_baseline]
					from [dbo].[sqlwatch_config_check_template] c
					where c.check_name = @check_name
				end

			if @expand_by = 'Disk'
				begin
					insert into @checks (
							[check_name],[check_description],[check_query],[check_frequency_minutes],[check_threshold_warning]
						   ,[check_threshold_critical],[check_enabled],[ignore_flapping],[check_template_id], [object_type], [object_name]
						   ,[target_sql_instance]
						   ,[use_baseline]
					)

					select 
					   [check_name]=case 
								when c.check_name like '%{Disk}%' then replace(c.check_name,'{Disk}',d.[volume_name]) 
								else c.check_name + ' (' + d.[volume_name] + ')' end 
					   ,[check_description]=case when [check_description] like '%{Disk}%'
												then replace([check_description],'{Disk}',d.[volume_name])
												else [check_description] end
					   ,[check_query]= replace(replace([check_query],'{Disk}',d.[volume_name]),'{SQL_INSTANCE}',@sql_instance)
					   ,[check_frequency_minutes]
					   ,[check_threshold_warning]
					   ,[check_threshold_critical]
					   ,[check_enabled]
					   ,[ignore_flapping]
					   ,[check_template_id]
					   ,[object_type] = @expand_by
					   ,[object_name] = d.[volume_name]
					   ,@sql_instance 
					   ,[use_baseline]
					from [dbo].[sqlwatch_config_check_template] c
					cross apply (
						select *
						from [dbo].[sqlwatch_meta_os_volume]
						where sql_instance = @sql_instance
						) d
					where c.check_name = @check_name
					and c.expand_by = @expand_by
					and d.sql_instance = @sql_instance
				end

			if @expand_by = 'Job'
				begin
					insert into @checks (
							[check_name],[check_description],[check_query],[check_frequency_minutes],[check_threshold_warning]
						   ,[check_threshold_critical],[check_enabled],[ignore_flapping],[check_template_id], [object_type], [object_name]
						   ,[target_sql_instance]
						   ,[use_baseline]
					)

					--this use to point to sysjobs hence the collate, I don't think we need collate anymore as within the db scope.
					select 
					   [check_name]=case 
								when c.check_name like '%{JOB}%' then replace(c.check_name,'{JOB}',d.[job_name] collate database_default) 
								else c.check_name + ' (' + d.[job_name] collate database_default + ')' end 
					   ,[check_description]=case when [check_description] like '%{JOB}%'
												then replace([check_description],'{JOB}',d.[job_name] collate database_default)
												else [check_description] end
					   ,[check_query]=replace(replace([check_query],'{JOB}',d.[job_name] collate database_default),'{SQL_INSTANCE}',@sql_instance)
					   ,[check_frequency_minutes]
					   ,[check_threshold_warning]
					   ,[check_threshold_critical]
					   ,[check_enabled]
					   ,[ignore_flapping]
					   ,[check_template_id]
					   ,[object_type] = @expand_by
					   ,[object_name] = d.[job_name]
					   ,@sql_instance
					   ,[use_baseline]
					from [dbo].[sqlwatch_config_check_template] c
					cross apply (
						select *
						from [dbo].[vw_sqlwatch_report_dim_agent_job]
						where sql_instance = @sql_instance
						) d
					where c.check_name = @check_name
					and c.expand_by = @expand_by
				end

			if @expand_by = 'Database'
				begin

					insert into @checks (
							[check_name],[check_description],[check_query],[check_frequency_minutes],[check_threshold_warning]
						   ,[check_threshold_critical],[check_enabled],[ignore_flapping],[check_template_id], [object_type], [object_name]
						   ,[target_sql_instance]
						   ,[use_baseline]
					)

					--this use to point to sys.databases hence the collate, I don't think we need collate anymore as within the db scope.
					select 
					   [check_name]=case 
								when c.check_name like '%{DATABASE}%' then replace(c.check_name,'{DATABASE}',d.[database_name] collate database_default) 
								else c.check_name + ' (' + d.[database_name] collate database_default + ')' end
					   ,[check_description]=case when [check_description] like '%{DATABASE}%'
												then replace([check_description],'{DATABASE}',d.[database_name] collate database_default)
												else [check_description] end
					   ,[check_query]=replace(replace([check_query],'{DATABASE}',d.[database_name] collate database_default),'{SQL_INSTANCE}',@sql_instance)
					   ,[check_frequency_minutes]
					   ,[check_threshold_warning]
					   ,[check_threshold_critical]
					   ,[check_enabled]
					   ,[ignore_flapping]
					   ,[check_template_id]
					   ,[object_type] = @expand_by
					   ,[object_name] = d.[database_name]
					   ,@sql_instance
					   ,[use_baseline]
					from [dbo].[sqlwatch_config_check_template] c
					cross apply (
						select *
						from [dbo].[sqlwatch_meta_database]
						where sql_instance = @sql_instance
						and is_current = 1
					) d
					where c.check_name = @check_name
					and c.expand_by = @expand_by
					and d.sql_instance = @sql_instance
				end

			fetch next from cur_expand_check 
				into @check_name, @expand_by, @check_template_id
		end

		close cur_expand_check
		deallocate cur_expand_check;

		fetch next from cur_expand_by_server
			into @sql_instance
	end

	close cur_expand_by_server
	deallocate cur_expand_by_server;


	;merge [dbo].[sqlwatch_config_check] as target 
	using @checks as source
	on target.check_name = source.check_name
	and target.[target_sql_instance] = source.[target_sql_instance]

	when not matched by target then
		insert (
				 [check_name]
				,[check_description]
				,[check_query]
				,[check_frequency_minutes]
				,[check_threshold_warning]
				,[check_threshold_critical]
				,[check_enabled]
				,[date_created]
				,[date_updated]
				,[ignore_flapping]
				,[check_template_id]
				,[base_object_type]
				,[base_object_name]
				,[base_object_date_last_seen]
				,[target_sql_instance]
				,[use_baseline]
				)
		values ( 
				 [check_name]
				,[check_description]
				,[check_query]
				,[check_frequency_minutes]
				,[check_threshold_warning]
				,[check_threshold_critical]
				,[check_enabled]
				,getutcdate()
				,getutcdate()
				,[ignore_flapping]
				,[check_template_id]
				,[object_type]
				,[object_name]
				,getutcdate()
				,[target_sql_instance]
				,[use_baseline]
				)

		when not matched by source 
		and target.[check_template_id] = @check_template_id then delete

		-- if the user sets the check as "user modified" we will not update it		
		when matched and isnull(target.user_modified,0) = 0 then 
			update 
				set [check_name] = source.[check_name]
				,[check_description] = source.[check_description]
				,[check_query] = source.[check_query]
				,[check_frequency_minutes] = source.[check_frequency_minutes]
				,[check_threshold_warning] = source.[check_threshold_warning]
				,[check_threshold_critical] = source.[check_threshold_critical]
				,[check_enabled] = source.[check_enabled]
				,[date_updated] = getutcdate()
				,[ignore_flapping] = source.[ignore_flapping]
				,[base_object_type] = source.[object_type]
				,[base_object_name] = source.[object_name]
				,[base_object_date_last_seen] = case when source.[object_name] is not null then getutcdate() else [base_object_date_last_seen] end
				,[use_baseline] = source.[use_baseline]
		;

		-- load action templates:
		merge [dbo].[sqlwatch_config_check_action] as target
		using (
			select 
				cc.[check_id]
			   ,a.[action_id]
			   ,a.[action_every_failure]
			   ,a.[action_recovery]
			   ,a.[action_repeat_period_minutes]
			   ,a.[action_hourly_limit]
			   ,a.[action_template_id]
			   ,[date_created]=GETUTCDATE()
			   ,[date_updated]=GETUTCDATE()
		
			from @checks c
			inner join [dbo].[sqlwatch_config_check_template]  ct
				on ct.check_template_id = c.check_template_id
			inner join [dbo].[sqlwatch_config_check_template_action] a
				on a.check_name = ct.check_name
			inner join [dbo].[sqlwatch_config_check] cc
				on cc.check_name = c.check_name
				and cc.target_sql_instance = c.target_sql_instance
			) as source
		on source.check_id = target.check_id
		and source.action_id = target.action_id

		when not matched then
			insert ([check_id]
           ,[action_id]
           ,[action_every_failure]
           ,[action_recovery]
           ,[action_repeat_period_minutes]
           ,[action_hourly_limit]
           ,[action_template_id]
           ,[date_created]
           ,[date_updated])
		   
		   values (
			source.[check_id]
           ,source.[action_id]
           ,source.[action_every_failure]
           ,source.[action_recovery]
           ,source.[action_repeat_period_minutes]
           ,source.[action_hourly_limit]
           ,source.[action_template_id]
           ,source.[date_created]
           ,source.[date_updated]
		   );

end
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_internal_foreachdb]
   @command nvarchar(max),
   @snapshot_type_id tinyint = null,
   @databases varchar(max) = 'ALL',
   @debug bit = 0,
   @calling_proc_id bigint = null,
   @ignore_global_exclusion bit = 0
as

/*
-------------------------------------------------------------------------------------------------------------------
 Procedure:
	usp_sqlwatch_internal_foreachdb

 Description:
	Iterate through databases i.e. improved replacement for sp_msforeachdb.

 Parameters
	@command	-	command to execute against each db, same as in sp_msforeachdb
	@snapshot_type_id	-	additionaly, if we are executing this in a collector, we can pass snapshot_id 
							in order to apply database/snapshot exlusion. This approach will prevent it
							from even accessing the database in the first place.
	@exlude_databases	-	list of comma separated database names to exclude from the loop
	
 Author:
	Marcin Gminski

 Change Log:
	1.0		2019-12		- Marcin Gminski, Initial version
	1.1		2019-12-10	- Marcin Gminski, database exclusion
	1.2		2019-12-23	- Marcin Gminski, added error handling and additional messaging
	1.3		2020-03-22	- Marcin Gminski, improved logging
	1.4		2020-03-23	- Marcin Gminski, added excplicit include 
	1.5		2020-06-26	- Marcin Gminski, print exec time
-------------------------------------------------------------------------------------------------------------------
*/
begin
	set nocount on;
	declare @sql nvarchar(max),
			@db	nvarchar(max),
			@exclude_from_loop bit,
			@has_errors bit = 0,
			@error_message nvarchar(max),
			@timestart datetime2(7),
			@timeend datetime2(7),
			@process_message nvarchar(max),
			@timetaken bigint

	set @process_message = 'Invoked by: [' + isnull(OBJECT_NAME(@calling_proc_id),'UNKNOWN') + '], @databases=' + @databases
	exec [dbo].[usp_sqlwatch_internal_log]
			@proc_id = @@PROCID,
			@process_stage = '5D318A4A-1F8A-4D44-B8B9-FFE2ECF62975',
			@process_message = @process_message,
			@process_message_type = 'INFO'


	declare @excludedbs table ([name] sysname)
	declare @includedbs table ([name] sysname)

	insert into @excludedbs
	select [value]
	from [dbo].[ufn_sqlwatch_split_string] (@databases,',') s
	where s.[value] like '-%' collate database_default

	insert into @includedbs
	select [value]
	from [dbo].[ufn_sqlwatch_split_string] (@databases,',') s
	where s.[value] not like '-%' collate database_default			

	declare cur_database cursor
	LOCAL FORWARD_ONLY STATIC READ_ONLY
	FOR 
	select distinct sdb.name
	from dbo.vw_sqlwatch_sys_databases sdb

	open cur_database
	fetch next from cur_database into @db

	while @@FETCH_STATUS = 0
		begin
			Print 'Processing database: ' + quotename(@db)
			-- check if database is excluded in [dbo].[sqlwatch_config_exclude_database]
			if not exists (
				select * from [dbo].[sqlwatch_config_exclude_database]
				where @db like [database_name_pattern]
				and snapshot_type_id = @snapshot_type_id
				and @ignore_global_exclusion = 0
				)
				begin
					-- check if database is excluded in @databases i.e. '-tempdb'
					if @databases = 'ALL'
						or (@databases <> 'ALL' and not exists (select * from @excludedbs where @db like right([name],len([name])-1)))
						or (@databases <> 'ALL' and not exists (select * from @excludedbs ))
						begin
							-- check if database is explicitly included in @databases i.e. 'master,msdb'
							if @databases = 'ALL'
								or (@databases <> 'ALL' and exists (select * from @includedbs where @db like [name]))
								or (@databases <> 'ALL' and not exists (select * from @includedbs))
								begin
									set @sql = ''
									set @sql = replace(@command,'?',@db)
									Print 'Executing command for database: ' + quotename(@db)
									begin try
										if @debug = 1
											begin
												Print @sql
											end
										set @timestart = SYSDATETIME()
										exec sp_executesql @sql
										set @timeend = SYSDATETIME()

										set @process_message = 'Processed database: [' + @db + '], @snapshot_type_id: ' + isnull(convert(nvarchar(max),@snapshot_type_id),'NULL') + '. Invoked by: [' + isnull(OBJECT_NAME(@calling_proc_id),'UNKNOWN') + '], time taken: '

										if datediff(s,@timestart,@timeend) <= 2147483648
											begin
												set @process_message  = @process_message  + convert(varchar(100),datediff(ms,@timestart,@timeend)) + 'ms'
											end
										else
											begin
												set @process_message  = @process_message  + convert(varchar(100),datediff(s,@timestart,@timeend)) + 's'
											end

										Print @process_message

										if dbo.ufn_sqlwatch_get_config_value(7, null) = 1
											begin
												exec [dbo].[usp_sqlwatch_internal_log]
														@proc_id = @@PROCID,
														@process_stage = '53BFB442-44CD-404F-8C2E-9203A04024D7',
														@process_message = @process_message,
														@process_message_type = 'INFO'
											end
									end try
									begin catch
										set @has_errors = 1
										if @@trancount > 0
											rollback

										exec [dbo].[usp_sqlwatch_internal_log]
												@proc_id = @@PROCID,
												@process_stage = 'F445D2BC-2CF3-4F41-9284-A4C3ACA513EB',
												@process_message = @sql,
												@process_message_type = 'ERROR'
										GoTo NextDatabase
									end catch
								end
							else
								begin
									Print 'A7F70FE7-D836-4B2D-A1CC-9E25D5F65180 Database (' + @db + ') not included in @databases (' + @databases + ')'
								end
							end
						else
							begin
								Print 'A6DFE8E3-607E-4E95-8C36-C2E23228B9A3 Database (' + @db + ') excluded from collection in @databases (' + @databases + ')'
							end
				end
			else
				begin
					Print '2F9BBB27-3606-4166-B699-F794140711C7 Database (' + @db + ') excluded from collection (snapshot_type_id: ' + isnull(convert(varchar(10), @snapshot_type_id),'NULL') + ') due to global exclusion.'
				end
			NextDatabase:
			fetch next from cur_database into @db
		end

		if @has_errors <> 0
			begin
				set @error_message = 'Errors during execution (' + OBJECT_NAME(@@PROCID) + ')'
				raiserror ('%s',16,1,@error_message)
			end
end
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_internal_get_last_snapshot_time]
	@sql_instance nvarchar(25),
	@snapshot_type_id smallint
AS
	select [snapshot_time] = isnull(max([snapshot_time]),'1970-01-01') from [dbo].[sqlwatch_logger_snapshot_header]
	where [sql_instance]= @sql_instance
	and [snapshot_type_id] = @snapshot_type_id
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_internal_get_last_snapshot_time_in_tables]
	@sql_instance nvarchar(25) = @@SERVERNAME
as
set nocount on;

declare @sql varchar(max)

declare @table_catalog nvarchar(128),
		@table_schema nvarchar(128),
		@table_name nvarchar(512),
		@table_type nvarchar(128)


create table #last_snapshot (
	sql_instance nvarchar(25),
	table_name nvarchar(512),
	snapshot_time datetime2(0)
)

create table #snapshot_id_table (
	table_name varchar(512),
	snapshot_type_id tinyint,
	snapshot_time datetime2(0),
	header_snapshot_time datetime2(0),
	sql_instance varchar(32)
	)

/* maintain relation between snapshots and tables */
insert into #snapshot_id_table(table_name, snapshot_type_id)
select *
from [dbo].[vw_sqlwatch_internal_table_snapshot]

update t
	--if we get null it means we have no snapshot at all.
	--at this stage this is a problem as we cannot import data without a snapshot
	--in this case, we have to default to the past to make sure no data is imported
	--to not violate referential integrity
	set header_snapshot_time = isnull(h.snapshot_time,'1970-01-01') 
		, sql_instance = @sql_instance
from #snapshot_id_table t
left join (
	select sql_instance, snapshot_type_id, snapshot_time=max(snapshot_time)
	from [dbo].[sqlwatch_logger_snapshot_header] h
	group by sql_instance, snapshot_type_id
	) h
	on h.snapshot_type_id = t.snapshot_type_id
	and h.sql_instance = @sql_instance



declare cur_tables cursor for
SELECT T.*
		FROM INFORMATION_SCHEMA.TABLES T
		INNER JOIN INFORMATION_SCHEMA.COLUMNS C
			ON T.TABLE_CATALOG = C.TABLE_CATALOG
			AND T.TABLE_SCHEMA = C.TABLE_SCHEMA
			AND T.TABLE_NAME = C.TABLE_NAME
		WHERE C.COLUMN_NAME = 'snapshot_time'
		AND T.TABLE_NAME like 'sqlwatch_logger%'
		AND T.TABLE_NAME not like 'sqlwatch_logger_snapshot_header'
		AND T.TABLE_TYPE = 'BASE TABLE'
		ORDER BY T.TABLE_NAME

open cur_tables  
fetch next from cur_tables
into @table_catalog, @table_schema,   @table_name, @table_type

while @@FETCH_STATUS = 0  
	begin
		select @sql = '
		select 
				sql_instance=''' + @sql_instance + '''
			,	table_name=''' + @table_name  + '''
			,   snapshot_time =max(snapshot_time)
			from ' + @table_name + ' h
			where h.sql_instance = ''' + @sql_instance + '''
'
		--print @sql
		insert into #last_snapshot
		exec (@sql)
		fetch next from cur_tables
		into @table_catalog, @table_schema,   @table_name, @table_type
	end



update t
	set snapshot_time = s.snapshot_time
from #snapshot_id_table t
	inner join #last_snapshot s
	on t.table_name = s.table_name

set @sql = ''
select @sql = @sql + ',' + table_name + ' = max(case when table_name = '''+ table_name +''' then convert(varchar(23),isnull(snapshot_time,''1970-01-01 00:00:00''),121) else null end)
' +
',' + table_name + '_header = max(case when table_name = '''+ table_name +''' then convert(varchar(23),isnull(header_snapshot_time,''1970-01-01 00:00:00''),121) else null end)
' 
from #snapshot_id_table

set @sql  = 'select sql_instance 
' + @sql + '
from #snapshot_id_table
group by sql_instance'

exec (@sql) 

/*	with result sets was introduced in SQL 2012. This will not build for SQL 2008.
	Remove if building for SQL 2012.	*/
with result sets (
 (		 
	 sql_instance varchar(32)
	,sqlwatch_logger_agent_job_history varchar(23)
	,sqlwatch_logger_agent_job_history_header varchar(23)
	,sqlwatch_logger_disk_utilisation_database varchar(23)
	,sqlwatch_logger_disk_utilisation_database_header varchar(23)
	,sqlwatch_logger_disk_utilisation_volume varchar(23)
	,sqlwatch_logger_disk_utilisation_volume_header varchar(23)
	,sqlwatch_logger_index_missing_stats varchar(23)
	,sqlwatch_logger_index_missing_stats_header varchar(23)
	,sqlwatch_logger_index_usage_stats varchar(23)
	,sqlwatch_logger_index_usage_stats_header varchar(23)
	,sqlwatch_logger_index_histogram varchar(23)
	,sqlwatch_logger_index_histogram_header varchar(23)
	,sqlwatch_logger_perf_file_stats varchar(23)
	,sqlwatch_logger_perf_file_stats_header varchar(23)
	,sqlwatch_logger_perf_os_memory_clerks varchar(23)
	,sqlwatch_logger_perf_os_memory_clerks_header varchar(23)
	,sqlwatch_logger_perf_os_performance_counters varchar(23)
	,sqlwatch_logger_perf_os_performance_counters_header varchar(23)
	,sqlwatch_logger_perf_os_process_memory varchar(23)
	,sqlwatch_logger_perf_os_process_memory_header varchar(23)
	,sqlwatch_logger_perf_os_schedulers varchar(23)
	,sqlwatch_logger_perf_os_schedulers_header varchar(23)
	,sqlwatch_logger_perf_os_wait_stats varchar(23)
	,sqlwatch_logger_perf_os_wait_stats_header varchar(23)
	,sqlwatch_logger_whoisactive varchar(23)
	,sqlwatch_logger_whoisactive_header varchar(23)
	,sqlwatch_logger_xes_blockers varchar(23)
	,sqlwatch_logger_xes_blockers_header varchar(23)
	,sqlwatch_logger_xes_iosubsystem varchar(23)
	,sqlwatch_logger_xes_iosubsystem_header varchar(23)
	,sqlwatch_logger_xes_long_queries varchar(23)
	,sqlwatch_logger_xes_long_queries_header varchar(23)
	,sqlwatch_logger_xes_query_processing varchar(23)
	,sqlwatch_logger_xes_query_processing_header varchar(23)
	,sqlwatch_logger_xes_waits_stats varchar(23)
	,sqlwatch_logger_xes_waits_stats_header varchar(23)
	,sqlwatch_logger_check varchar(23)
	,sqlwatch_logger_check_header varchar(23)
	,sqlwatch_logger_check_action varchar(23)
	,sqlwatch_logger_check_action_header varchar(23)
	,sqlwatch_logger_disk_utilisation_table varchar(23)
	,sqlwatch_logger_disk_utilisation_table_header varchar(23)
	)
)
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_internal_get_query_plans]
	@plan_handle utype_plan_handle readonly,
	@sql_instance varchar(32)
AS
	set nocount on;
	set xact_abort on;

	/*  
		The idea is to store query plans and statements based on the query_hash and query_plan_hash.
		This will greatly reduce the number of stored plans but would mean that the query_plan may differ slightly to the one executed.
		https://docs.microsoft.com/en-us/sql/relational-databases/system-dynamic-management-views/sys-dm-exec-query-stats-transact-sql
		
		query_hash		Binary hash value calculated on the query and used to identify queries with similar logic. 

		query_plan_hash	Binary hash value calculated on the query execution plan and used to identify similar query execution plans. 
						Will always be 0x000 when a natively compiled stored procedure queries a memory-optimized table.

		so for example, two queries:
			"select * from table where date = 'date1'"
			"select * from table where date = 'date2'"
		will likely have different plan_handle and sql_handle but the same hash and we will only store the first one we encounter for the combination of 
		query hash and plan hash

		In addition, queries that have null or 0x000 hash will be stored in the [dbo].[sqlwatch_meta_query_plan_handle] 
		As there should be less of these than those with hash, we are going to hopefully save alot of storage and still provide useful data.
		this will be configurable in via config_table where users will be able to switch on/off where to save plans based on their workloads
		or disable if the tables get too big etc.
	*/

	declare @get_plans bit = dbo.ufn_sqlwatch_get_config_value(22,null),
			@date_now datetime2(0) = getutcdate(),
			@sqlwatch_plan_id_output dbo.utype_plan_id;

	with cte_plans as (
		select 
			  RN_HANDLE = ROW_NUMBER() over (partition by ph.plan_handle, qs.query_plan_hash order by (select null))
			, ph.[plan_handle]
			, qs.[sql_handle]
			, query_hash = qs.query_hash
			, query_plan_hash = qs.query_plan_hash
			, ph.statement_start_offset
			, ph.statement_end_offset
			, [statement] = substring(t.text, (ph.statement_start_offset/2)+1,((case qs.statement_end_offset
							when -1 then datalength(t.text)
							else qs.statement_end_offset
							end - qs.statement_start_offset)/2) + 1)
			, qp.query_plan
			, sql_instance = @sql_instance
			, [database_name] = db_name(qp.dbid)
			, [procedure_name] = isnull(object_schema_name(qp.objectid, qp.dbid) + '.' + object_name (qp.objectid, qp.[dbid]),'Ad-Hoc Query 3FBE6AA6')
		from @plan_handle ph

		inner join sys.dm_exec_query_stats qs 
			on ph.[plan_handle] = qs.[plan_handle]
			and ph.[statement_start_offset] = qs.[statement_start_offset]
			and ph.[statement_end_offset] = qs.[statement_end_offset]
			-- The idea is to also match on the sql_handle if present but I am not sure that we need to do this.
			and qs.[sql_handle] = case when ph.[sql_handle] is not null then ph.[sql_handle] else qs.[sql_handle] end

		cross apply sys.dm_exec_text_query_plan(ph.[plan_handle], ph.[statement_start_offset], ph.[statement_end_offset]) qp
	
		cross apply sys.dm_exec_sql_text(qs.sql_handle) t

		where qp.[encrypted] = 0
		and t.[encrypted] = 0
		and @get_plans = 1
		and ph.plan_handle <> 0x0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000
		and ph.statement_start_offset is not null
		and ph.statement_end_offset is not null
	)

	select 
		  p.RN_HANDLE
		, RN_HASH = ROW_NUMBER() over (partition by p.sql_instance, query_plan_hash order by (select null))
		, p.[plan_handle]
		, p.[sql_handle]
		, p.query_hash
		, p.query_plan_hash 
		, p.statement_start_offset
		, p.statement_end_offset
		, p.[statement] 
		, p.query_plan
		, p.sql_instance 
		, mp.sqlwatch_procedure_id
		, mdb.sqlwatch_database_id
	into #plans
	from cte_plans p

	inner join [dbo].[sqlwatch_meta_database] mdb
		on mdb.[database_name] = p.[database_name] collate database_default
		and mdb.is_current = 1
		and mdb.sql_instance = @sql_instance

	inner join [dbo].[sqlwatch_meta_procedure] mp
		on mp.sql_instance = @sql_instance
		and mp.[procedure_name] = p.[procedure_name] collate database_default
		and mp.sqlwatch_database_id = mdb.sqlwatch_database_id;

	create unique clustered index idx_tmp_plans on #plans ([plan_handle], [sql_handle], [query_hash]
		, [query_plan_hash], [sql_instance], sqlwatch_procedure_id, sqlwatch_database_id, RN_HANDLE, RN_HASH
		, statement_start_offset, statement_end_offset);

	merge [dbo].[sqlwatch_meta_query_plan] as target
	using (
		select distinct 
			sql_instance ,
			[plan_handle],
			[sql_handle] ,
			[query_hash] ,
			[query_plan_hash] ,
			[statement_start_offset] ,
			[statement_end_offset],
			sqlwatch_procedure_id,
			sqlwatch_database_id,
			[query_plan] = case when ([query_plan_hash] is null or [query_plan_hash] = 0x00) then query_plan else null end,
			[statement] = case when [query_plan_hash] is null or [query_plan_hash] = 0x00 then [statement] else null end
		from #plans
	) as source
		on source.sql_instance = target.sql_instance
		and source.[plan_handle] = target.[plan_handle]
		and source.[statement_start_offset] = target.[statement_start_offset]
		and source.[statement_end_offset] = target.[statement_end_offset]
		and source.sqlwatch_procedure_id = target.sqlwatch_procedure_id
		and source.sqlwatch_database_id = target.sqlwatch_database_id
	
	when matched then 
		update set date_last_seen = @date_now

	when not matched then
		insert (  [sql_instance] 
				, [plan_handle]
				, [sql_handle] 
				, [query_hash] 
				, [query_plan_hash] 
				, [statement_start_offset] 
				, [statement_end_offset]
				, [date_first_seen]
				, [date_last_seen]
				, sqlwatch_procedure_id
				, sqlwatch_database_id
				, [query_plan_for_plan_handle]
				, [statement_for_plan_handle]
				)
		values (  source.sql_instance 
				, source.[plan_handle]
				, source.[sql_handle]
				, source.[query_hash]
				, source.[query_plan_hash] 
				, source.[statement_start_offset]
				, source.[statement_end_offset]
				, @date_now
				, @date_now
				, source.sqlwatch_procedure_id
				, source.sqlwatch_database_id
				, source.[query_plan]
				, source.[statement]
				)
		;

	merge dbo.[sqlwatch_meta_query_plan_hash] as target
	using (
		select 
			  [sql_instance]
			, [query_plan_hash]
			, [statement]
			, [query_plan]
			--, [statement_start_offset]
			--, [statement_end_offset]
		from #plans 
		where RN_HASH = 1
		and [query_plan_hash] not in (0x000,0x00)

	)as source
	on target.[query_plan_hash] = source.[query_plan_hash]
	and target.[sql_instance] = source.[sql_instance]

	when matched then 
		update set 
			date_last_seen = @date_now

	when not matched then
		insert ( 
			  [sql_instance]
			, [query_plan_hash]
			, [statement_for_query_plan_hash]
			, [query_plan_for_query_plan_hash]
			, [date_first_seen]
			, [date_last_seen]
			--, [statement_start_offset]
			--, [statement_end_offset]
			)
		values (
			  source.[sql_instance]
			, source.[query_plan_hash]
			, source.[statement]
			, source.query_plan 
			, @date_now
			, @date_now
			--, source.[statement_start_offset]
			--, source.[statement_end_offset]
			)
		;
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_internal_get_xes_data]
	@session_name nvarchar(64),
	@object_name nvarchar(256) = null,
	@min_interval_s int = 1,
	@last_event_time datetime = null --to be removed
AS

set nocount on;

declare @results table (
	event_data xml,
	object_name nvarchar(256),
	event_time datetime
);

if [dbo].[ufn_sqlwatch_get_product_version]('major') < 11
	begin
		exec [dbo].[usp_sqlwatch_internal_log]
			@proc_id = @@PROCID,
			@process_stage = '56FE7588-B8F4-49C5-A40D-167AC6067919',
			@process_message = 'Product version must be 11 or higher to use Extended Events',
			@process_message_type = 'WARNING';

		--we havve to return empty resultset back to the caller:
		select event_data, object_name, event_time
		from @results;

		return;
	end;

--The execution count is per session, not per session's object_name.
--This means that we may still run the collector because the session has trigger but it has not logged our particular object.
--This is clearly visible in the system_health where session triggers roughly even 1 minute but the sp_server_diagnostics_component_result object 
--is only logged every 5 minutes. I have added a parameter @min_interval_s that we can pass to skip the collector if the data diff is less.
--Ideally we shuold just schdule the collector to run less often
declare @xes_last_captured_execution_count bigint,
		@xes_current_execution_count bigint,
		@xes_last_captured_event_time datetime,
		@address varbinary(8),
		@event_file  varchar(128),
		@xes_current_last_event_time datetime;
		
select @xes_last_captured_execution_count = execution_count
	,  @xes_last_captured_event_time = isnull(last_event_time,'1970-01-01')
from [dbo].[sqlwatch_stage_xes_exec_count]
where session_name = @session_name
option (keep plan);

--bail out if we're checking too often:
if datediff(second,@xes_last_captured_event_time,getutcdate()) < @min_interval_s
	begin
		select event_data, object_name, event_time
		from @results;

		return;
	end;

--we're getting session address in a separate batch
--becuase when we join xe_sessions with xe_session_targets
--the execution goes up to 500ms. two batches run in 4 ms.
select @address = address 
from sys.dm_xe_sessions with (nolock)
where name = @session_name
option (keepfixed plan);

--having it all in a single place will improve performance and allow getting rid of some of the user functions:
select 
		@xes_current_execution_count = isnull(execution_count,0)
	,	@event_file = convert(xml,[target_data]).value('(/EventFileTarget/File/@name)[1]', 'varchar(8000)')
from sys.dm_xe_session_targets with (nolock)
where event_session_address = @address
and target_name = 'event_file'
option (keepfixed plan);

--bail out if the xes has not triggered since last run:
if (@xes_current_execution_count <= @xes_last_captured_execution_count)
	begin
		select event_data, object_name, event_time
		from @results;
		return;
	end;

with cte_event_data as (
	select 
		  event_data=convert(xml,event_data)
		, t.object_name
		, event_time = [dbo].[ufn_sqlwatch_get_xes_timestamp]( event_data )
	from sys.fn_xe_file_target_read_file (@event_file, null, null, null) t
	where @object_name is null 
		or (
			@object_name is not null 
			and object_name = @object_name
			)
)
insert into @results
select event_data, object_name, event_time
from cte_event_data
where event_time > @xes_last_captured_event_time;

--get last event_time:
select @xes_current_last_event_time = max(event_time)
from @results;

--update execution count
--this is quite optimistic here as once we have gotten the xes data out of xml, 
--we are going to update the table so the next pull will be from this point.
--However, if calling logger query fails and does not insert the data into the logger table
--we could lose this data. It is important to handle transactions in a way that everything is 
--rolled back when error occurs.
--An alternative would be to run this update after the data has been inserted (which we actually used to do) 
--but it would require two things: to remember to do it (yay repetition), 
--and to get the last event time which is not available in the calling query this is balancing a risk vs convinience. 
--maybe you should not be taking an example from this if you're building a nuclear reactor management system.
update [dbo].[sqlwatch_stage_xes_exec_count]
set  execution_count = @xes_current_execution_count
	, last_event_time = isnull(@xes_current_last_event_time,getutcdate())
where session_name = @session_name
option (keep plan);

--return data:
select event_data, object_name, event_time
from @results;
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_internal_insert_header]
	@snapshot_time_new datetime2(0) OUTPUT ,
	@snapshot_type_id tinyint
as

begin

	set xact_abort on;
	set nocount on;

	declare @snapshot_time datetime2(0) = convert(datetime2(0),GETUTCDATE()),
			@sql_instance varchar(32) = dbo.ufn_sqlwatch_get_servername()

	declare @snapshot_time_output table (
		snapshot_time datetime2(0)
	);


	insert into [dbo].[sqlwatch_logger_snapshot_header] ([snapshot_time], [snapshot_type_id], [sql_instance], [report_time])
	output inserted.[snapshot_time] into @snapshot_time_output ( snapshot_time )
	select  [snapshot_time] = @snapshot_time,
			[snapshot_type_id] = @snapshot_type_id,
			[sql_instance] = @sql_instance, 
			[report_time] = dateadd(mi, datepart(TZOFFSET,SYSDATETIMEOFFSET()), (CONVERT([smalldatetime],dateadd(minute,ceiling(datediff(second,(0),CONVERT([time],CONVERT([datetime],@snapshot_time)))/(60.0)),datediff(day,(0),@snapshot_time)))))
	except
	select [snapshot_time], [snapshot_type_id], [sql_instance], [report_time]
	from [dbo].[sqlwatch_logger_snapshot_header]
	where snapshot_time = @snapshot_time
	and snapshot_type_id = snapshot_type_id
	and sql_instance = @sql_instance

	option (keep plan)

	if (@@ROWCOUNT = 0)
		begin
			--we already have this snapshot time
			select @snapshot_time_new = @snapshot_time
		end
	else
		begin	
			select @snapshot_time_new = snapshot_time from @snapshot_time_output
		end

	if @snapshot_time_new  is null
		begin
			raiserror ('Fatal error: Variable @snapshot_time must not be null. Possible issue with acquiring an application lock.',16,1)
		end
end
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_internal_log]
	@process_name			nvarchar(max) = null,
	@process_stage			nvarchar(max),
	@process_message		nvarchar(max),
	@process_message_type	nvarchar(max),
	@proc_id				int = null
as

SET XACT_ABORT ON
SET NOCOUNT ON 

begin try	

	declare @snapshot_time datetime2(0)

	if @process_message_type = 'INFO' and dbo.ufn_sqlwatch_get_config_value(7, null) <> 1
		begin
			return
		end

	if @process_name is null and @proc_id is not null
		begin
			set @process_name = OBJECT_NAME(@proc_id)
		end

	insert into dbo.[sqlwatch_app_log] (
		 [process_name]			
		,[process_stage]			
		,[process_message]		
		,[process_message_type]	
		,[spid]					
		,[process_login]			
		,[process_user]			
		,[ERROR_NUMBER]
		,[ERROR_SEVERITY]
		,[ERROR_STATE]
		,[ERROR_PROCEDURE]
		,[ERROR_LINE]
		,[ERROR_MESSAGE]
	)
	values (
		@process_name, @process_stage, @process_message, @process_message_type,
		@@SPID, SYSTEM_USER, USER
			, case when @process_message_type <> 'INFO' then ERROR_NUMBER() else null end
			, case when @process_message_type <> 'INFO' then ERROR_SEVERITY() else null end
			, case when @process_message_type <> 'INFO' then ERROR_STATE() else null end
			, case when @process_message_type <> 'INFO' then ERROR_PROCEDURE() else null end
			, case when @process_message_type <> 'INFO' then ERROR_LINE() else null end
			, case when @process_message_type <> 'INFO' then ERROR_MESSAGE() else null end
	)

--	begin
--		Print char(10) + '>>>---- ' + @process_message_type + ' ------------------------------------' + char(10) +
--'Time: ' + convert(nvarchar(23),@snapshot_time,121) + char(10) + 
--'Process Name: ' + @process_name + char(10) + 
--'Stage: ' + @process_stage + char(10) +  
--'Message: ' + @process_message + char(10) + 
--'Message Type: ' + @process_message_type + char(10) + 
--case when @process_message_type <> 'INFO' then 'Error: ' + [dbo].[ufn_sqlwatch_get_error_detail_text] () else '' end + char(10) +
--'---- ' + @process_message_type + ' ------------------------------------<<<' + char(10)
--	end 

end try
begin catch
	--fatal error in the error loggin procedure
	declare @message nvarchar(max)
	set @message = [dbo].[ufn_sqlwatch_get_error_detail_text] ()
	raiserror (@message,16,1)
	print @message
end catch
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_internal_migrate_jobs_to_queues]
as

--this procedure will disable and remove the relevent agent jobs and enable broker based collection
declare @sql varchar(max) = '',
		@database_name sysname = db_name()

select @sql = @sql  + ';' + char(10) + 'exec msdb.dbo.sp_delete_job @job_id=N'''+ convert(varchar(255),job_id) +''', @delete_unused_schedule=1' 
from msdb.dbo.sysjobs
where name like case when @database_name <> 'SQLWATCH' then 'SQLWATCH-\[' + @database_name + '\]%' else 'SQLWATCH-%' end  escape '\'
  and name not like case when @database_name = 'SQLWATCH' then 'SQLWATCH-\[%' else '' end  escape '\'
and name not like '%AZMONITOR'
and name not like '%ACTIONS'
and name not like '%DISK-UTILISATION'
and name not like '%INDEXES'
and name not like '%WHOISACTIVE'

exec (@sql);


-- activate queues:
exec [dbo].[usp_sqlwatch_internal_restart_queues];

-- update config so the next deployment is aware of the migration:
update dbo.sqlwatch_config
set config_value = 1
where config_id = 13
and config_value = 0
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_internal_process_actions] (
	@sql_instance varchar(32) = @@SERVERNAME,
	@check_id smallint,
	@action_id smallint,
	@check_status varchar(50),
	@check_value decimal(28,5),
	@check_description nvarchar(max) = null,
	@check_name nvarchar(max),
	@check_threshold_warning varchar(100) = null,
	@check_threshold_critical varchar(100) = null,
	@snapshot_time datetime2(0),
	@snapshot_type_id tinyint,
	@is_flapping bit = 0
	)
as

SET NOCOUNT ON 

/* 
-------------------------------------------------------------------------------------------------------------------
	[usp_sqlwatch_internal_process_actions]

	Abstract: 

	actions expect the following parameters:
	{SUBJECT} and {BODY}

	however, each can have its own template.
	for example, when using email action, we could have more content in the body
	and when using pushover we could limit it to most important informations.


	Version:
		1.0 2019-11--- - Marcin Gminski
------------------------------------------------------------------------------------------------------------------- 
*/

declare @action_type varchar(200) = 'NONE',
		@action_every_failure bit,
		@action_recovery bit,
		@action_repeat_period_minutes smallint,
		@action_hourly_limit tinyint,
		@action_template_id smallint,
		@last_action_time datetime,
		@action_count_last_hour smallint,
		@subject nvarchar(max),
		@body nvarchar(max),
		@subject_template nvarchar(max),
		@body_template nvarchar(max),
		@report_id smallint,
		@content_info varbinary(128),
		@error_message nvarchar(max),
		@error_message_single nvarchar(max) = '',
		@has_errors bit = 0,
		@action_template_type varchar(max),
		@action_exec_type varchar(max),
		@error_message_xml xml

--need to this so we can detect the caller in [usp_sqlwatch_internal_process_reports] to avoid circular ref.
select @content_info = convert(varbinary(128),convert(varchar(max),@action_id))
set CONTEXT_INFO @content_info

-------------------------------------------------------------------------------------------------------------------
-- Get action parameters:
-------------------------------------------------------------------------------------------------------------------
select 
		@action_every_failure = cca.[action_every_failure]
	,	@action_recovery = cca.[action_recovery]
	,	@action_repeat_period_minutes = cca.[action_repeat_period_minutes]
	,	@action_hourly_limit = cca.[action_hourly_limit]
	,	@action_template_id = cca.[action_template_id]
	,	@action_exec_type = ca.[action_exec_type]
from [dbo].[sqlwatch_config_check_action] cca
	inner join [dbo].[sqlwatch_config_action] ca
		on ca.action_id = cca.action_id
where cca.[check_id] = @check_id
and cca.[action_id] = @action_id

-------------------------------------------------------------------------------------------------------------------
-- each check has limit of actions per hour to avoid flooding:
-------------------------------------------------------------------------------------------------------------------
select 
	  @last_action_time = max([snapshot_time])
	, @action_count_last_hour = sum(case when [snapshot_time] > dateadd(hour,-1,getutcdate()) then 1 else 0 end)
from [dbo].[sqlwatch_logger_check_action]
where [check_id] = @check_id
and [action_id] = @action_id
and sql_instance = @@SERVERNAME


-------------------------------------------------------------------------------------------------------------------
-- skip actions for flapping cheks unless we expliclity want to action every failure:
-------------------------------------------------------------------------------------------------------------------
if @is_flapping = 1
	begin
		if @action_every_failure = 0
			begin
				--information only:
				set @error_message = 'Check (Id: ' + convert(varchar(10),@check_id) + ') Is flapping. Action (Id: ' + convert(varchar(10),@action_id) + ') is skipped.'
				exec [dbo].[usp_sqlwatch_internal_log]
						@proc_id = @@PROCID,
						@process_stage = '1D779244-0524-44B1-A00B-19BDA355D4EE',
						@process_message = @error_message,
						@process_message_type = 'WARNING'
				GoTo LogAction	
			end
		else
			begin
				set @error_message = 'Check (Id: ' + convert(varchar(10),@check_id) + ') Is flapping but @action_every_failure is set to 1. Action (Id: ' + convert(varchar(10),@action_id) + ') will be performed.'
				exec [dbo].[usp_sqlwatch_internal_log]
						@proc_id = @@PROCID,
						@process_stage = '43A6F442-2272-4953-81E7-B7014212BA29',
						@process_message = @error_message,
						@process_message_type = 'INFO'
			end
	end

-------------------------------------------------------------------------------------------------------------------
-- Get action details and add to the queue:
-------------------------------------------------------------------------------------------------------------------
if @action_count_last_hour > @action_hourly_limit
	begin
		--information only:
		set @error_message = 'Check (Id: ' + convert(varchar(10),@check_id) + '): Action (Id: ' + convert(varchar(10),@action_id) + ') has exceeded hourly allowed limit and it will not be performed.'
		exec [dbo].[usp_sqlwatch_internal_log]
				@proc_id = @@PROCID,
				@process_stage = '76C7745B-CDD2-4545-AF42-A3A5636D3F46',
				@process_message = @error_message,
				@process_message_type = 'WARNING'
		GoTo LogAction
	end


-------------------------------------------------------------------------------------------------------------------
-- We need to know if we are dealing with a new, repeated or recovered action. For this, we have to check
-- previous checks where action was requested
-------------------------------------------------------------------------------------------------------------------
select @action_type = case 

	-------------------------------------------------------------------------------------------------------------------
	-- when the current status is not OK and the previous status was OK, its a new notification:
	-------------------------------------------------------------------------------------------------------------------
	when @check_status <> 'OK' and isnull(last_check_status,'OK') = 'OK' then 'NEW'

	-------------------------------------------------------------------------------------------------------------------
	-- if previous status is NOT ok and current status is OK the check has recovered from fail to success.
	-- we can send an email notyfing DBAs that the problem has gone away
	-------------------------------------------------------------------------------------------------------------------
	when @check_status = 'OK' and isnull(last_check_status,'OK') <> 'OK' and @action_recovery = 1 then 'RECOVERY'

	-------------------------------------------------------------------------------------------------------------------
	-- retrigger if the value has changed and the status is not OK
	-- this is handy if we want to monitor every change after it has failed. for example we can set to monitor
	-- if number of logins is greater than 5 so if someone creates a new login we will get an email and then every time
	-- new login is created

	-- this however will not work in situations where we want a notification for ongoing blocking chains or failed jobs
	-- where there the count does not change. i.e. job A fails and then recovers but job B fails instead. The overall 
	-- count of jailed jobs at any given time is still 1. in such instance we can ste a reminder to 1 minute.
	-------------------------------------------------------------------------------------------------------------------
	when @check_status <> 'OK' and isnull(last_check_status,'OK') <> 'OK' 
		 and (last_check_value is null or @check_value <> last_check_value) and @action_every_failure = 1 then 'REPEAT'

	-------------------------------------------------------------------------------------------------------------------
	-- if the previous status is the same as the current status we would not normally send another email
	-- however, we can do if we set retrigger time. for example, we can be sending repeated alerts every hour so 
	-- they do not get forgotten about. 
	-------------------------------------------------------------------------------------------------------------------
	when @check_status <> 'OK' and last_check_status = @check_status and (@action_repeat_period_minutes is not null 
		and datediff(minute,isnull(@last_action_time,'1970-01-01'),getdate()) > @action_repeat_period_minutes) then 'REPEAT'

	else 'NONE' end
from [dbo].[sqlwatch_meta_check]
where [check_id] = @check_id
and sql_instance = @@SERVERNAME

-------------------------------------------------------------------------------------------------------------------
-- now we know what action we are dealing with, we can build template:
-------------------------------------------------------------------------------------------------------------------
select 
	 @subject_template = case @action_type 
			when 'NEW' then action_template_fail_subject
			when 'RECOVERY' then action_template_recover_subject
			when 'REPEAT' then action_template_repeat_subject
		else 'UNDEFINED' end
	,@body_template = case @action_type
			when 'NEW' then action_template_fail_body
			when 'RECOVERY' then action_template_recover_body
			when 'REPEAT' then action_template_repeat_body
		else 'UNDEFINED' end
	,@action_template_type = action_template_type
from [dbo].[sqlwatch_config_check_action_template]
where action_template_id = @action_template_id
--and sql_instance = @@SERVERNAME

/*  email clients do not handle <code> tags well so if we have any of these custom <codetable> in the description we will replace 
	with the below table. This is only so we do not store any HTML tags in the descriptions as they get pulled into PBI and can look ugly.
	And it makes writing html description easier. In the future this may get parameterised. */
set @check_description = 
	case when @action_template_type = 'HTML' then
		replace(
			replace(@check_description,
				'<code>','<table border=0 width="100%" cellpadding="10" style="display:block;background:#ddd; margin-top:1em;white-space: pre;"><tr><td><pre>'),
			'</code>','</pre></td></tr></table>')
		else @check_description end

-------------------------------------------------------------------------------------------------------------------
-- set {SUBJECT} and {BODY}
-------------------------------------------------------------------------------------------------------------------
if @action_type  <> 'NONE'
	begin
		--an action with arbitrary executable must have the following parameters:
		--{SUBJECT} and {BODY}
		--on top of it, an action will have a template that can have one of the below parameters so need to substitute them here:
		select @subject = 
			replace(
				replace(
					replace(
						replace(
							replace(
								replace(
									replace(
										replace(
											replace(
												replace(
													replace(
														replace(
															replace(
																replace(
																	replace(@subject_template,'{CHECK_STATUS}',@check_status)
																,'{CHECK_NAME}',check_name)
															,'{SQL_INSTANCE}',@@SERVERNAME)
														,'{CHECK_ID}',convert(varchar(max),cc.check_id))
													,'{CHECK_STATUS}',@check_status)
												,'{CHECK_VALUE}',convert(varchar(max),@check_value))
											,'{CHECK_LAST_VALUE}',isnull(convert(varchar(max),cc.last_check_value),'N/A'))
										,'{CHECK_LAST_STATUS}',isnull(cc.last_check_status,'N/A'))
									,'{LAST_STATUS_CHANGE}',isnull(convert(varchar(max),cc.last_status_change_date,121),'Never'))
								,'{CHECK_TIME}',convert(varchar(max),getdate(),121))
							,'{THRESHOLD_WARNING}',isnull(cc.check_threshold_warning,''))
						,'{THRESHOLD_CRITICAL}',isnull(cc.check_threshold_critical,''))
					,'{CHECK_DESCRIPTION}',isnull(rtrim(ltrim(case 
						when @action_exec_type = 'T-SQL' then replace(cc.check_description,'''','''''')
						when @action_exec_type = 'PowerShell' then replace(cc.check_description,'"','`"')
						end)),''))
				,'{CHECK_QUERY}',isnull(rtrim(ltrim(case
						when @action_exec_type = 'T-SQL' then replace(cc.check_query,'''','''''')
						when @action_exec_type = 'PowerShell' then replace(cc.check_query,'"','`"')
						end)),''))
			,'{SQL_VERSION}',@@VERSION)

			, @body = 
			replace(
				replace(
					replace(
						replace(
							replace(
								replace(
									replace(
										replace(
											replace(
												replace(
													replace(
														replace(
															replace(
																replace(
																	replace(@body_template,'{CHECK_STATUS}',@check_status)
																,'{CHECK_NAME}',check_name)
															,'{SQL_INSTANCE}',@@SERVERNAME)
														,'{CHECK_ID}',convert(varchar(max),cc.check_id))
													,'{CHECK_STATUS}',@check_status)
												,'{CHECK_VALUE}',convert(varchar(max),@check_value))
											,'{CHECK_LAST_VALUE}',isnull(convert(varchar(max),cc.last_check_value),'N/A'))
										,'{CHECK_LAST_STATUS}',isnull(cc.last_check_status,'N/A'))
									,'{LAST_STATUS_CHANGE}',isnull(convert(varchar(max),cc.last_status_change_date,121),'Never'))
								,'{CHECK_TIME}',convert(varchar(max),getdate(),121))
							,'{THRESHOLD_WARNING}',isnull(cc.check_threshold_warning,'None'))
						,'{THRESHOLD_CRITICAL}',isnull(cc.check_threshold_critical,''))
					,'{CHECK_DESCRIPTION}',isnull(rtrim(ltrim(case 
						when @action_exec_type = 'T-SQL' then replace(cc.check_description,'''','''''')
						when @action_exec_type = 'PowerShell' then replace(cc.check_description,'"','`"')
						end)),''))
				,'{CHECK_QUERY}',isnull(rtrim(ltrim(case
						when @action_exec_type = 'T-SQL' then replace(cc.check_query,'''','''''')
						when @action_exec_type = 'PowerShell' then replace(cc.check_query,'"','`"')
						end)),''))
			,'{SQL_VERSION}',@@VERSION)
			

		from [dbo].[sqlwatch_meta_check] cc
		where cc.check_id = @check_id
		and cc.sql_instance = @@SERVERNAME

		insert into [dbo].[sqlwatch_meta_action_queue] (sql_instance, [action_exec_type], [action_exec])
		select @@SERVERNAME, [action_exec_type], replace(replace([action_exec],'{SUBJECT}',@subject),'{BODY}',@body)
		from [dbo].[sqlwatch_config_action]
		where action_id = @action_id
		and [action_enabled] = 1
		and [action_exec] is not null --null action exec can only be for reports but they are processed below

		--is this action calling a report or an arbitrary exec?
		select @report_id = action_report_id 
		from [dbo].[sqlwatch_config_action] where action_id = @action_id

		if @report_id is not null
			begin
				--if we have action that calls a report, call the report here:
				begin try
					exec [dbo].[usp_sqlwatch_internal_process_reports] 
						 @report_id = @report_id
						,@check_status = @check_status
						,@check_value = @check_value
						,@check_name = @check_name
						,@subject = @subject
						,@body = @body
						,@check_threshold_warning = @check_threshold_warning
						,@check_threshold_critical = @check_threshold_critical
				end try
				begin catch
					set @has_errors = 1		
					set @error_message = 'Action (Id:' + convert(varchar(10),@action_id) + ') calling Report (Id: ' + convert(varchar(10),@report_id) + ')'
					exec [dbo].[usp_sqlwatch_internal_log]
						@proc_id = @@PROCID,
						@process_stage = 'F7A4AA65-1BE9-4D0B-8B1F-054CA1E24A6E',
						@process_message = @error_message,
						@process_message_type = 'ERROR'

					--select @error_message_xml = [dbo].[ufn_sqlwatch_get_error_detail_xml](
					--	@@PROCID,'F7A4AA65-1BE9-4D0B-8B1F-054CA1E24A6E','exec [dbo].[usp_sqlwatch_internal_process_reports] @report_id=' + convert(varchar(10),@report_id) + ' @action_id=' + convert(varchar(10),@action_id)
					--	)
				end catch
			end
	end

 LogAction:

--log action for each check. This is so we can track how many actions are being executed per each check to satisfy 
--the [action_hourly_limit] parameter and to have an overall visibility of what checks trigger what actions. 
--This table needs a minimum of 1 hour history.
if @action_type <> 'NONE'
	begin
		insert into [dbo].[sqlwatch_logger_check_action] ([sql_instance], [snapshot_type_id], [check_id], [action_id], [snapshot_time], [action_attributes])
		select @@SERVERNAME, @snapshot_type_id, @check_id, @action_id, @snapshot_time, (
			select *
			from (
				select	'ContentInfo' = @action_id,
						'ActionEveryFailure' = @action_every_failure,
						'ActionRecovery' = @action_recovery,
						'ActionRepeatPeriodMinutes' = @action_repeat_period_minutes,
						'ActionHourlyLimit' = @action_hourly_limit,
						'ActionTemplateId' = @action_template_id,
						'LastActionTime' = @last_action_time,
						'ActionCountLastHour' = @action_count_last_hour,
						'ActionType' = @action_type,
						'ReportId' = @report_id,
						'Subject' = @subject,
						'Body' = @body
			) a
			for xml path('Attributes'))
	end

if @has_errors = 1
	begin
		set @error_message = 'Errors during action execution (' + OBJECT_NAME(@@PROCID) + '): 
' + @error_message

		--print all errors and terminate the batch which will also fail the agent job for the attention:
		raiserror ('%s',16,1,@error_message)
	end
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_internal_process_checks] 
AS
/*
-------------------------------------------------------------------------------------------------------------------
 usp_sqlwatch_internal_process_alerts

 Change Log:
	1.0 2019-11-03 - Marcin Gminski
-------------------------------------------------------------------------------------------------------------------
*/

SET NOCOUNT ON 
SET DATEFORMAT ymd --fix for non EN formats

declare @check_name nvarchar(100),
		@check_description nvarchar(2048),
		@check_query nvarchar(max),
		@check_warning_threshold varchar(100),
		@check_critical_threshold varchar(100),
		@check_query_instance varchar(32),
		@check_id smallint,
		@check_start_time datetime2(7),
		@check_end_time datetime2(7),
		@check_exec_time_ms real,
		@actions xml,
		@sql_instance varchar(32) = dbo.ufn_sqlwatch_get_servername(),
		@use_baseline bit,
		@baseline_id smallint,
		@check_baseline real,
		@i tinyint,
		@i_len tinyint,
		@check_critical_threshold_baseline varchar(100),
		@check_baseline_variance smallint = [dbo].[ufn_sqlwatch_get_config_value] ( 17, null ),
		@check_variance smallint = [dbo].[ufn_sqlwatch_get_config_value] ( 18, null ),
		@target_sql_instance varchar(32);

declare @check_status varchar(50),
		@check_value decimal(28,5),
		@last_check_status varchar(50),
		@previous_value decimal(28,5),
		@last_status_change datetime,
		@retrigger_time smallint,
		@last_trigger_time datetime,
		@trigger_date datetime,
		@send_recovery bit,
		@send_email bit = 1,
		@retrigger_on_every_change bit,
		@target_type varchar(50),
		@error_message nvarchar(max) = '',
		@trigger_limit_hour tinyint, --max number of messages per hour
		@trigger_current_count smallint,
		@error_message_single nvarchar(max) = '',
		@error_message_xml xml,
		@has_errors bit = 0,
		-- Where I say variant, I mean deviation. Bit of a brain fart.
		@actual_variance_check decimal(28,5),
		@actial_variance_check_baseline decimal(28,5),
		@deviation_from_default_threshold real,
		@deviation_from_baseline_threshold real,
		@deviatoin_from_value_default real,
		@deviation_from_value_baseline real

declare @email_subject nvarchar(255),
		@email_body nvarchar(4000),
		@target_attributes nvarchar(255),
		@recipients nvarchar(255),
		@msg_payload nvarchar(max)

declare @action_id smallint,
		@subject nvarchar(max),
		@body nvarchar(max),
		@previous_check_date datetime, 
		@previous_check_value real, 
		@previous_check_status varchar(50),
		@check_time datetime,
		@ignore_flapping bit,
		@is_flapping bit


declare @snapshot_type_id tinyint = 18,
		@snapshot_type_id_action tinyint = 19,
		@snapshot_time datetime2(0) = getutcdate(),
		@snapshot_time_action datetime2(0)

declare @mail_return_code int

exec [dbo].[usp_sqlwatch_internal_insert_header] 
	@snapshot_time_new = @snapshot_time OUTPUT,
	@snapshot_type_id = @snapshot_type_id

declare cur_rules cursor LOCAL STATIC for
select 
	  cc.[check_id]
	, cc.[check_name]
	, cc.[check_description]
	, cc.[check_query]
	, [check_threshold_warning] = case 
			when cc.[check_threshold_warning] like '%{LAST_CHECK_VALUE}%' and mc.last_check_value is not null 
			then replace(cc.[check_threshold_warning],'{LAST_CHECK_VALUE}',mc.last_check_value) 
			else mc.[check_threshold_warning] 
		end
	, [check_threshold_critical] = case 
			when cc.[check_threshold_critical] like '%{LAST_CHECK_VALUE}%' and mc.last_check_value is not null 
			then replace(cc.[check_threshold_critical],'{LAST_CHECK_VALUE}',mc.last_check_value) 
			else cc.[check_threshold_critical] 
		end
	-- this used to be isnull(mc.last_check_date,dateadd(day,-1,'1970-01-01'))
	-- but this meant that if we ever recreated checks after few months, the first would run evaluate ALL of the data.
	-- we are going to limit this to only last day
	, last_check_date = isnull(mc.last_check_date,dateadd(day,-1,getutcdate()))
	, mc.last_check_value
	, mc.last_check_status
	, cc.[ignore_flapping]
	, cc.use_baseline
	, cc.target_sql_instance
from [dbo].[sqlwatch_config_check] cc

inner join [dbo].[sqlwatch_meta_check] mc
	on mc.check_id = cc.check_id
	and mc.sql_instance = @sql_instance

where cc.[check_enabled] = 1
and datediff(minute,isnull(mc.last_check_date,'1970-01-01'),getutcdate()) >=
		-- when check has failed to execute, we are going to repeat it after 1 hour (this should be a global config)
		case when mc.last_check_status = 'CHECK ERROR' and isnull(mc.[check_frequency_minutes],0) > dbo.ufn_sqlwatch_get_config_value(12,null)
		then dbo.ufn_sqlwatch_get_config_value(12,null) 
		else isnull(mc.[check_frequency_minutes],0)
		end

order by cc.[check_id]

open cur_rules
  
fetch next from cur_rules 
into @check_id, @check_name, @check_description , @check_query, @check_warning_threshold, @check_critical_threshold
	, @previous_check_date, @previous_check_value, @previous_check_status, @ignore_flapping, @use_baseline, @target_sql_instance


while @@FETCH_STATUS = 0  
begin
	
	set @check_status = null
	set @check_value = null
	set @actions = null
	set @error_message = ''
	set @is_flapping = 0

	-------------------------------------------------------------------------------------------------------------------
	-- execute check and log output in variable:
	-- APP_STAGE: 5980A79A-D6BC-4BA0-8B86-A388E8DB621D
	-------------------------------------------------------------------------------------------------------------------
	set @check_start_time = SYSUTCDATETIME()

	begin try
		set @check_query = 'SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED
SET NOCOUNT ON 
SET ANSI_WARNINGS OFF
' + replace(@check_query,'{LAST_CHECK_DATE}',convert(varchar(23),@previous_check_date,121))
		exec sp_executesql @check_query, N'@output decimal(28,5) OUTPUT', @output = @check_value output;
		set @check_end_time = SYSUTCDATETIME()
		set @check_exec_time_ms = convert(real,datediff(MICROSECOND,@check_start_time,@check_end_time) / 1000.0 )
		if @check_value is null
			begin
				set @error_message = 'Unable to evaluate thresholds because Check (Id: ' + convert(varchar(10),@check_id) + ') has returned NULL value'
				raiserror (@error_message, 16, 1)
			end
	end try
	begin catch

		if @error_message is null or @error_message = '' 
			begin 
				set @error_message = replace(@check_query,'%','%%')
			end
		else
			begin
				set @error_message = @error_message + '
--- Query -------------------------------------------------
' + replace(@check_query,'%','%%') + ''
			end

		if dbo.ufn_sqlwatch_get_config_value(8, null) <> 0
			begin
				set @has_errors = 1

				select FAILED_QUERY = @check_query, ERROR_MESSAGE = ERROR_MESSAGE()				

				exec [dbo].[usp_sqlwatch_internal_log]
					@proc_id = @@PROCID,
					@process_stage = '5980A79A-D6BC-4BA0-8B86-A388E8DB621D',
					@process_message = @error_message,
					@process_message_type = 'ERROR'
			end
		else
			begin
				exec [dbo].[usp_sqlwatch_internal_log]
					@proc_id = @@PROCID,
					@process_stage = 'ED7B7EC1-6F0A-4B23-909E-7BB1D37B300D',
					@process_message = @error_message,
					@process_message_type = 'WARNING'
			end


		update	[dbo].[sqlwatch_meta_check]
		set last_check_date = isnull(@check_end_time,SYSUTCDATETIME()),
			last_check_status = 'CHECK ERROR'
		where [check_id] = @check_id
		and sql_instance = @sql_instance

		set @error_message = 'CheckID : ' + convert(varchar(10),@check_id)
						
		insert into [dbo].[sqlwatch_logger_check] (sql_instance, snapshot_time, snapshot_type_id, check_id, 
			[check_value], [check_status], check_exec_time_ms)
		values (@sql_instance, @snapshot_time, @snapshot_type_id, @check_id, null, 'CHECK ERROR', @check_exec_time_ms)
			
		goto ProcessNextCheck
	end catch


	-------------------------------------------------------------------------------------------------------------------
	--	Check for flapping
	--  needs some work to be more reliable
	--  we take last 12 checks (based on 5 minute check = 60 minutes)
	--  and calculate change ratio. result 0.5 will mean exact number of failures and OK which means flapping.
	--  to give it some leaway, we say ignore if betwen 0.35 and 0.65.
	--	this approach is far from ideal but will do for now. if causing trouble it can be disabled in config_check
	-------------------------------------------------------------------------------------------------------------------
	if @ignore_flapping = 0 and (
						select avg(convert(decimal(10,2),status_change))
							from (
								select top 12 *
								from dbo.[sqlwatch_logger_check] lc
								where snapshot_time > dateadd(hour,-6,getutcdate())
								and sql_instance = @sql_instance
								and check_id = @check_id
								and snapshot_type_id = @snapshot_type_id
								order by snapshot_time desc
							) t
						) between 0.35 and 0.65
		begin
			set @is_flapping = 1
			--warning only:
			if (dbo.ufn_sqlwatch_get_config_value(11,null) = 1)
				begin
					set @error_message = 'Check (Id: ' + convert(varchar(10),@check_id) + ') is flapping.'
					exec [dbo].[usp_sqlwatch_internal_log]
							@proc_id = @@PROCID,
							@process_stage = '040D0A86-83B8-4543-A34C-9F328DAE5488',
							@process_message = @error_message,
							@process_message_type = 'WARNING'
				end

		end

	-------------------------------------------------------------------------------------------------------------------
	-- set check status based on the output:
	-- there are 3 basic options: OK, WARNING and CRITICAL.
	-- the critical could be greater or lower, or just different than the success for example:
	--	1. we can have an alert to trigger if someone drops database. in that case the critical would be less than desired value
	--	2. we can have a trigger if someone creates new databsae in which case, the critical would be greater than desired value
	--	3. we can have a trigger that checks for a number of databases and any change is critical either greater or lower.
	-------------------------------------------------------------------------------------------------------------------

	--since we can reference last check value in the threshold as a parameter, we have to account for the first run, where the previous value does not exist. 
	--In such situation the threshold cannot be compared to and we have to return an OK (as we dont know if the value is out of bounds). 
	--The second iteration should then be able to compare to the previous value and return the desired status
	--If we have {LAST_CHECK_VALUE} value at this point, it means there is no previous check value.
	begin try

		if @check_critical_threshold like '%{LAST_CHECK_VALUE}%' or @check_warning_threshold like '%{LAST_CHECK_VALUE}%' 
			begin
				set @check_status =	'OK'  
			end
		else
			begin
				set @error_message = FORMATMESSAGE('Determining @check_status for %s (id: %i).', @check_name,@check_id)
				--The baseline will take precedence over values in [check_threshold_warning] and [check_threshold_critical].
				if @use_baseline = 1 
					begin
						set @error_message = @error_message + ' We will try to use baseline data.'
						-- If we are asked to use the baseline and if have a default baseline, get value from the baseline data 
						-- (in this case, the baseline data means the check that had previously run and has been baselined:
						-- when using baseline, we're going to set it as critical. in the future we will also set warning based on % of baseline or even based on another baseline
						select @check_baseline=[dbo].[ufn_sqlwatch_get_check_baseline](
							@check_id
							,null --get default baseline
							,@sql_instance)		

						if @check_baseline is not null
							begin
								set @error_message = @error_message + FORMATMESSAGE(' We have got a baseline value of %s.'
									,convert(varchar(50),@check_baseline)
									)

								select @check_critical_threshold_baseline = left(@check_critical_threshold,patindex('%[0-9]%',@check_critical_threshold)-1)+convert(varchar(50),@check_baseline)
							end
						else
							begin
								set @error_message = @error_message + FORMATMESSAGE(' We have NOT got any baseline data.')
							end

						if @check_critical_threshold_baseline is not null
							begin

								set @error_message = @error_message + FORMATMESSAGE(' We have set the critical threshold from baseline value of %s.'
									,@check_critical_threshold_baseline
									)

								if dbo.ufn_sqlwatch_get_config_value ( 16, null ) = 1
									begin
										set @error_message = @error_message + FORMATMESSAGE(' We are running strict baselining. The check value is %s, and the threshold from the baseline is %s'
											,convert(varchar(50),@check_value)
											,@check_critical_threshold_baseline
											)

										-- if strict baselining, only compare baseline check with no variance:
										if [dbo].[ufn_sqlwatch_get_check_status] ( @check_critical_threshold_baseline, @check_value, 1 ) = 1
											begin
												set @error_message = @error_message + FORMATMESSAGE(' Setting @check_status to CRITICAL.')
												set @check_status = 'CRITICAL'
											end
										else
											begin
												set @error_message = @error_message + FORMATMESSAGE(' Setting @check_status to OK.')
												set @check_status = 'OK'
											end
									end
								else
									begin
										set @actual_variance_check = null
										set @actial_variance_check_baseline = null

										select @actual_variance_check = [dbo].[ufn_sqlwatch_get_threshold_deviation](	@check_critical_threshold,	@check_variance )
										select @actial_variance_check_baseline = [dbo].[ufn_sqlwatch_get_threshold_deviation](	@check_critical_threshold_baseline,	@check_baseline_variance )

										set @error_message = @error_message + 
										FORMATMESSAGE(' We are running relaxed baselining. We are going to compare against either the baseline threshold or the default threshold. The result will be OK if either returns OK. 
The check value is %s, the baseline threshold is %s, and the default threshold is %s. The baseline variance of %s%% and default variance of %s%% set the threshold to %s%s and %s%s respectively.
If the check satisfies either of these thresholds we are going to set the check result to OK.'
											,convert(varchar(50),@check_value)
											,@check_critical_threshold_baseline
											,@check_critical_threshold
											,convert(varchar(50),@check_baseline_variance)
											,convert(varchar(50),@check_variance)
											,[dbo].[ufn_sqlwatch_get_threshold_comparator](@check_critical_threshold_baseline)
											,convert(varchar(50),@actial_variance_check_baseline)
											,[dbo].[ufn_sqlwatch_get_threshold_comparator](@check_critical_threshold)
											,convert(varchar(50),@actual_variance_check)
											)

										-- if relaxed baselining, check both and pick more optimistic value.
										if [dbo].[ufn_sqlwatch_get_check_status] ( @check_critical_threshold_baseline, @check_value, @check_baseline_variance ) = 0
										or [dbo].[ufn_sqlwatch_get_check_status] ( @check_critical_threshold, @check_value, @check_variance ) = 0
											begin
												set @error_message = @error_message + FORMATMESSAGE(' Either the baseline or the default check has returned OK.')
												set @check_status = 'OK'
											end
										else
											begin
												set @error_message = @error_message + FORMATMESSAGE(' Neither the baseline nor the default check has returned OK so setting CRITICAL.')
												set  @check_status =  'CRITICAL'
											end
									end
							end
					end --@use_baseline = 1 
				else
					begin
						set @error_message = @error_message + FORMATMESSAGE(' We are NOT using baseline data for this check.')
					end

					--if @check_status is still null, it means the baseline based comparison has not set it.
					--it could be because we told it to use baseline but there was no baseline.
					if @check_status is null
						begin
							set @error_message = @error_message + 
							FORMATMESSAGE(' The @check_status is null. The check value is %s, the warning threshold is %s, and critical threshold is %s. The variance is 1.'
								,convert(varchar(50),@check_value)
								,@check_warning_threshold
								,@check_critical_threshold
								)

							if [dbo].[ufn_sqlwatch_get_check_status] ( @check_critical_threshold, @check_value, 1 ) = 1
								begin
									set @error_message = @error_message + FORMATMESSAGE(' The final result is CRITICAL.')
									set @check_status =  'CRITICAL'
								end
							else if [dbo].[ufn_sqlwatch_get_check_status] ( @check_warning_threshold, @check_value, 1 ) = 1
								begin
									set @error_message = @error_message + FORMATMESSAGE(' The final result is WARNING.')
									set @check_status =  'WARNING'
								end
							else
								begin
									set @error_message = @error_message + FORMATMESSAGE(' The final result is OK.')
									set @check_status =  'OK'
								end
						end


					--we have baseline, use it
					--set @error_message = 'Check (Id: ' + convert(varchar(10),@check_id) + ') baseline value (' + @check_critical_threshold_baseline + ')'
					exec [dbo].[usp_sqlwatch_internal_log]
							@proc_id = @@PROCID,
							@process_stage = '55C51822-5204-42B0-97A6-039608B9ACB8',
							@process_message = @error_message,
							@process_message_type = 'INFO'

				end
	end try
	begin catch

		set @has_errors = 1				
		set @error_message = FORMATMESSAGE('Errors when setting check_status for for Check (Id: %i)
The parameters were:
dbo.ufn_sqlwatch_get_config_value ( 16, null ): %i
@check_value: %s
@use_baseline: %i
@check_critical_threshold_baseline: %s
@check_baseline_variance: %i
@check_critical_threshold: %s
@check_variance: %i
@check_warning_threshold: %s
'
			,@check_id
			,dbo.ufn_sqlwatch_get_config_value ( 16, null )
			,convert(varchar(50),@check_value)
			,convert(int,@use_baseline)
			,@check_critical_threshold_baseline
			,@check_baseline_variance
			,@check_critical_threshold
			,@check_variance
			,@check_warning_threshold
		)

		exec [dbo].[usp_sqlwatch_internal_log]
			@proc_id = @@PROCID,
			@process_stage = 'D17BF7E2-55FC-4B96-ABE3-8BD299924B6B',
			@process_message = @error_message,
			@process_message_type = 'ERROR'

			goto ProcessNextCheck

	end catch


	----if @check_status is still null then check if its warning, but we may not have warning so need to account for that:
	--select @check_status = case when @check_status is null 
	--			and @check_warning_threshold is not null 
	--			and [dbo].[ufn_sqlwatch_get_check_status] ( @check_warning_threshold, @check_value ) = 1 then 'WARNING' else @check_status end

	----if not warninig or critical then OK
	--if @check_status is null
	--	set @check_status = 'OK'

	-------------------------------------------------------------------------------------------------------------------
	-- log check results:
	-------------------------------------------------------------------------------------------------------------------
	insert into [dbo].[sqlwatch_logger_check] (sql_instance, snapshot_time, snapshot_type_id, check_id, 
		[check_value], [check_status], check_exec_time_ms, [status_change], [is_flapping]
		, baseline_threshold
		)
	values (@sql_instance, @snapshot_time, @snapshot_type_id, @check_id, @check_value, @check_status, @check_exec_time_ms, 
		case when isnull(@check_status,'') <> isnull(@previous_check_status,'') then 1 else 0 end
		, @is_flapping 
		, convert(real,dbo.ufn_sqlwatch_get_threshold_value(@check_critical_threshold_baseline))
		)
		
	-------------------------------------------------------------------------------------------------------------------
	-- process any actions for this check but only if status not OK or previous status was not OK (so we can process recovery)
	-- if current and previous status was OK we wouldnt have any actions anyway so there is no point calling the proc.
	-- assuming 99% of time all checks will come back as OK, this will save significant CPU time
	-------------------------------------------------------------------------------------------------------------------
	if @check_status <> 'OK' or @previous_check_status <> 'OK'
		begin
			declare cur_actions cursor for
			select cca.[action_id]
				from [dbo].[sqlwatch_config_check_action] cca
					--so we only try process actions that are enabled:
					inner join [dbo].[sqlwatch_config_action] ca
						on cca.action_id = ca.action_id
				where cca.check_id = @check_id
				and ca.action_enabled = 1
				order by cca.check_id

				open cur_actions

				if @@CURSOR_ROWS <> 0
					begin
						/*	logging header here so we only get one header for the batch of actions
							datetime2(0) has a resolution of 1 second and if we had multuple actions, the below
							procedure would have iterated quicker that that causing PK violation on insertion of the subsequent action headers	*/
							exec [dbo].[usp_sqlwatch_internal_insert_header] 
								@snapshot_time_new = @snapshot_time_action OUTPUT,
								@snapshot_type_id = @snapshot_type_id_action

						--Print 'Processing actions for check.'
					end
 
				fetch next from cur_actions 
				into @action_id

				while @@FETCH_STATUS = 0  
					begin
						begin try						
							exec [dbo].[usp_sqlwatch_internal_process_actions] 
								@sql_instance = @sql_instance,
								@check_id = @check_id,
								@action_id = @action_id,
								@check_status = @check_status,
								@check_value = @check_value,
								@check_description = @check_description,
								@check_name = @check_name,
								@check_threshold_warning = @check_warning_threshold,
								@check_threshold_critical = @check_critical_threshold,
								@snapshot_time = @snapshot_time_action,
								@snapshot_type_id = @snapshot_type_id_action,
								@is_flapping = @is_flapping
						end try
						begin catch
							--28B7A898-27D7-44C0-B6EB-5238021FD855
							set @has_errors = 1				
							set @error_message = 'Errors when processing Action (Id: ' + convert(varchar(10),@action_id) + ') for Check (Id: ' + convert(varchar(10),@check_id) + ')'
							exec [dbo].[usp_sqlwatch_internal_log]
								@proc_id = @@PROCID,
								@process_stage = '28B7A898-27D7-44C0-B6EB-5238021FD855',
								@process_message = @error_message,
								@process_message_type = 'ERROR'
							GoTo NextAction
						end catch

						NextAction:
						fetch next from cur_actions 
						into @action_id
					end

			close cur_actions
			deallocate cur_actions
		end

	-------------------------------------------------------------------------------------------------------------------
	-- update meta with the latest values.
	-- we have to do this after we have triggered actions as the [usp_sqlwatch_internal_process_actions] needs
	-- previous values
	-------------------------------------------------------------------------------------------------------------------
	update	[dbo].[sqlwatch_meta_check]
	set last_check_date = @check_end_time,
		last_check_value = @check_value,
		last_check_status = @check_status,
		last_status_change_date = case when @previous_check_status <> @check_status then getutcdate() else last_status_change_date end
	where [check_id] = @check_id
	and sql_instance = @sql_instance

	ProcessNextCheck:

	fetch next from cur_rules 
	into @check_id, @check_name, @check_description , @check_query, @check_warning_threshold, @check_critical_threshold
		, @previous_check_date, @previous_check_value, @previous_check_status, @ignore_flapping, @use_baseline, @target_sql_instance
	
end

close cur_rules
deallocate cur_rules


if @has_errors = 1
	begin
		set @error_message = 'Errors during execution (' + OBJECT_NAME(@@PROCID) + ')'
		--print all errors and terminate the batch which will also fail the agent job for the attention:
		raiserror ('%s',16,1,@error_message)
	end
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_internal_process_reports] (
	@report_batch_id varchar(255) = null,
	@report_id smallint = null,
	@check_status nvarchar(50) = null,
	@check_value decimal(28,5) = null,
	@check_name nvarchar(max) = null,
	@subject nvarchar(max) = null,
	@body nvarchar(max) = null,
	--so we can apply filter to the reports:
	@check_threshold_warning varchar(100) = null,
	@check_threshold_critical varchar(100) = null
	)
as
/*
-------------------------------------------------------------------------------------------------------------------
 [usp_sqlwatch_internal_process_reports]

 Change Log:
	1.0 2019-11-03 - Marcin Gminski
-------------------------------------------------------------------------------------------------------------------
*/
SET NOCOUNT ON 
SET ANSI_NULLS ON


if @report_batch_id is null and @report_id is null
	begin
		raiserror('Either @report_batch_id or @report_id required',16,1)
	end

declare @sql_instance varchar(32),
		@report_title varchar(255),
		@report_description varchar(4000),
		@report_definition nvarchar(max),
		@delivery_target_id smallint,
		@definition_type varchar(25),

		@delivery_command nvarchar(max),
		@target_address nvarchar(max),
		@action_exec nvarchar(max),
		@action_exec_type nvarchar(max),
		@action_id smallint,

		@css nvarchar(max),
		@html nvarchar(max),
		@snapshot_type_id tinyint = 20,
		@snapshot_time datetime2(0),

		@error_message nvarchar(max) = '',
		@has_errored bit = 0,
		@report_last_run_date datetime,
		@report_current_run_date datetime,
		@report_current_run_date_utc datetime

declare @sqlwatch_logger_report_action table (
	 [sql_instance] varchar(32)
	,[snapshot_time] datetime2(0)
	,[snapshot_type_id] tinyint
	,[report_id] smallint
	,[action_id] smallint
	,[error_message] xml
)

exec [dbo].[usp_sqlwatch_internal_insert_header] 
	@snapshot_time_new = @snapshot_time OUTPUT,
	@snapshot_type_id = @snapshot_type_id

declare cur_reports cursor for
select cr.[report_id]
      ,cr.[report_title]
      ,cr.[report_description]
      ,cr.[report_definition]
	  ,cr.[report_definition_type]
	  ,t.[action_exec]
	  ,t.[action_exec_type]
	  ,isnull(rs.style,'')
	  ,ra.action_id
	  ,mr.report_last_run_date
  from [dbo].[sqlwatch_config_report] cr

  inner join [dbo].[sqlwatch_config_report_action] ra
	on cr.report_id = ra.report_id

	inner join dbo.[sqlwatch_config_action] t
	on ra.[action_id] = t.[action_id]

	inner join [dbo].[sqlwatch_meta_report] mr
		on mr.report_id = cr.report_id
		and mr.sql_instance = @@SERVERNAME

	left join [dbo].[sqlwatch_config_report_style] rs
		on rs.report_style_id = cr.report_style_id

  where [report_active] = 1
  and t.[action_enabled] = 1

  --and isnull([report_batch_id],0) = isnull(@report_batch_id,0)
  --and cr.report_id = isnull(@report_id,cr.report_id)
  --avoid getting a report that calls actions that has called this routine to avoid circular refernce:
    and convert(varchar(128),ra.action_id) <> isnull(convert(varchar(128),CONTEXT_INFO()),'0')

  --we must either run report by id or by batch. a null batch_id will indicate that we only run report by its id, usually triggred by an action
  --a batch_id indicates that we run reports from a batch job, i.e. some daily scheduled server summary reports etc, something that is not triggered by an action.
  --remember, an action is triggred on the back of a failed check so unsuitable for a "scheduled daily reports"

  and case /* no batch id passed, we are runing individual report */ when @report_batch_id is null then convert(varchar(255),@report_id) else @report_batch_id end = 
	case when @report_batch_id is null then convert(varchar(255),cr.[report_id]) else cr.[report_batch_id] end


order by cr.report_id

open cur_reports

fetch next from cur_reports
into @report_id, @report_title, @report_description, @report_definition, @definition_type, @action_exec, @action_exec_type, @css, @action_id, @report_last_run_date

while @@FETCH_STATUS = 0  
	begin

		delete from @sqlwatch_logger_report_action
		set @html = ''

		select @report_current_run_date = GETDATE(), @report_current_run_date_utc = GETUTCDATE()

		select @report_definition = replace(
										replace(
											replace(@report_definition,'{REPORT_LAST_RUN_DATE}',convert(varchar(23),@report_last_run_date,121)
											),'{REPORT_CURRENT_RUN_DATE}',convert(varchar(23),@report_current_run_date,121)
										),'{REPORT_CURRENT_RUN_DATE_UTC}',convert(varchar(23),@report_current_run_date_utc,121)
									)
		 
		/*	Query type does not get processed but is being passed straight into action for further processing i.e.
			in case we want to extract data to file:
			Invoke-SqlCmd -Query "{BODY}" | Out-File -Path .....
			Or for Azure Log Monitor Extractor	*/
		if @definition_type = 'Query'
			begin
				select 
					@body = @report_definition,
					@subject = @report_title

					GoTo QueueAction
			end



		if @check_status is not null
			begin
				set @report_definition = case 
					when @check_status = 'CRITICAL' and @check_threshold_critical is not null then replace(@report_definition,'{THRESHOLD}',@check_threshold_critical)
					when @check_status = 'WARNING' and @check_threshold_warning is not null then replace(@report_definition,'{THRESHOLD}',@check_threshold_warning)
					else @report_definition end
			end

		/*	Table type must be a single T-SQL query that will be converted into a HTML table	*/
		if @definition_type in ('HTML-Table','Table')
			begin
				begin try
					exec [dbo].[usp_sqlwatch_internal_query_to_html_table] @html = @html output, @query = @report_definition
				end try
				begin catch
					set @has_errored = 1
					set @error_message = 'Error when executing Query Report (usp_sqlwatch_internal_query_to_html_table), @report_batch_id: ' + isnull(convert(varchar(max),@report_batch_id),'NULL') + ', @report_id: ' + isnull(convert(varchar(max),@report_id),'NULL')
					exec [dbo].[usp_sqlwatch_internal_log]
							@proc_id = @@PROCID,
							@process_stage = '31FF6B08-735E-45F9-BAAB-D1F7E446BB1B',
							@process_message = @error_message,
							@process_message_type = 'ERROR'

					insert into @sqlwatch_logger_report_action ([sql_instance],[snapshot_time],[snapshot_type_id],[report_id],[action_id])
					select @@SERVERNAME,@snapshot_time,@snapshot_type_id,@report_id,@action_id

					GoTo NextReport
				end catch
			end

		/*	Template type is complex template that must produce an output ready to be passed into action, 
			i.e. a complete html report	*/
		if @definition_type in ('HTML-Template', 'Template')
			begin
				begin try
					exec sp_executesql @report_definition, N'@output nvarchar(max) OUTPUT', @output = @html output;
				end try
				begin catch
					--E3796F4B-3C89-450E-8FC7-09926979074F
					set @has_errored = 1
					set @error_message = 'Error when executing Template Report (usp_sqlwatch_internal_query_to_html_table), @report_batch_id: ' + isnull(convert(varchar(max),@report_batch_id),'NULL') + ', @report_id: ' + isnull(convert(varchar(max),@report_id),'NULL')
					exec [dbo].[usp_sqlwatch_internal_log]
							@proc_id = @@PROCID,
							@process_stage = 'E3796F4B-3C89-450E-8FC7-09926979074F',
							@process_message = @error_message,
							@process_message_type = 'ERROR'

					insert into @sqlwatch_logger_report_action ([sql_instance],[snapshot_time],[snapshot_type_id],[report_id],[action_id])
					select @@SERVERNAME,@snapshot_time,@snapshot_type_id,@report_id,@action_id

					GoTo NextReport
				end catch
			end

		select @css, @html
		set @html = '<html><head><style>' + @css + '</style><body>' + @html

		--if @check_name is NOT null it means report has been triggered by a check action. Therefore, we need to respect the check action template:
		if charindex('{REPORT_CONTENT}',isnull(@body,'')) = 0
			begin
				--body content was either not passed or does not contain '{REPORT_CONTENT}'. In this case we are just going to include the report as the body.
				set @body = @html + case when @report_description is not null then '<p>' + @report_description + '</p>' else '' end 
				set @subject = @report_title
			end
		else
			begin
				set @body = replace(
								replace(
									replace(@body,'{REPORT_CONTENT}',isnull(@html,'Report Id: ' + convert(varchar(10),@report_id) + ' contains no data.'))
								,'{REPORT_TITLE}',@report_title)
							,'{REPORT_DESCRIPTION}',@report_description)
							
				set @subject = replace(@subject,'{REPORT_TITLE}',@report_title)
			end

		/*	If check is null it means we are not triggered report from the check.
			and if type = Query it means we are running a simple query. in this case
			add footer. 
			
			However, if we are here from the check or from "Template" based report, 
			the footers (as the whole content) are customisaible in the template */
		if @definition_type = 'Table' and @check_name is null 
			begin
				set @body = @body + '<p>Email sent from SQLWATCH on host: ' + @@SERVERNAME +'
		<a href="https://sqlwatch.io">https://sqlwatch.io</a></p></body></html>'
			end

		QueueAction:

		update [dbo].[sqlwatch_meta_report]
			set [report_last_run_date] = @report_current_run_date_utc
		where [sql_instance] = @@SERVERNAME
		and [report_id] = @report_id

		set @action_exec = case @action_exec_type 
			/* for sql actions we have to escape quotes */
			when 'T-SQL' then replace(replace(@action_exec,'{BODY}', replace(@body,'''','''''')),'{SUBJECT}',@subject)
			else replace(replace(@action_exec,'{BODY}',@body),'{SUBJECT}',@subject)
		end

		if @action_exec is null
			begin
				Print 'Report (Id: ' + convert(varchar(255),@report_id) + ') @action_exec is NULL (Id: ' + convert(varchar(255),@action_id) + ')'
				GoTo NextReport
			end

		--now insert into the delivery queue for further processing:
		insert into [dbo].[sqlwatch_meta_action_queue] ([sql_instance], [time_queued], [action_exec_type], [action_exec])
		values (@@SERVERNAME, sysdatetime(), @action_exec_type, @action_exec)

		Print 'Item ( Id: ' + convert(varchar(10),SCOPE_IDENTITY()) + ' ) queued.'

		--E3796F4B-3C89-450E-8FC7-09926979074F
		insert into @sqlwatch_logger_report_action ([sql_instance],[snapshot_time],[snapshot_type_id],[report_id],[action_id])
		select @@SERVERNAME,@snapshot_time,@snapshot_type_id,@report_id,@action_id

		NextReport:

		insert into [dbo].[sqlwatch_logger_report_action]
		select [sql_instance], [snapshot_time], [snapshot_type_id], [report_id], [action_id] 
		from @sqlwatch_logger_report_action

		fetch next from cur_reports 
		into @report_id, @report_title, @report_description, @report_definition, @definition_type, @action_exec, @action_exec_type, @css, @action_id, @report_last_run_date

	end

close cur_reports
deallocate cur_reports


if @has_errored = 1
	begin
		set @error_message = 'Errors during execution of (' + OBJECT_NAME(@@PROCID) + '). Please review action log.'
		raiserror ('%s',16,1,@error_message)
	end
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_internal_purge_deleted_items]
as
declare @sql varchar(max),
		@purge_after_days tinyint,
		@row_batch_size int

set @purge_after_days = [dbo].[ufn_sqlwatch_get_config_value]  (2, null)
set @row_batch_size = [dbo].[ufn_sqlwatch_get_config_value]  (5, null)
set @sql = 'declare @rows_affected bigint'

select @sql = @sql + '
delete top (' + convert(varchar(10),@row_batch_size) + ') from ' + TABLE_SCHEMA + '.' + TABLE_NAME + '
where ' + COLUMN_NAME + ' < dateadd(day,-' + convert(varchar(10),@purge_after_days) +',getutcdate())
and ' + COLUMN_NAME + ' is not null
set @rows_affected = @@ROWCOUNT

Print ''Purged '' + convert(varchar(10),@rows_affected) + '' rows from ' + TABLE_SCHEMA + '.' + TABLE_NAME + ' ''
'
 from INFORMATION_SCHEMA.COLUMNS
/*	I should have been more careful when naming columns, I ended up having all these variations.
	The exception is base_object_date_last_seen which is different to date_last_seen as it referes to a parent object rather than row in the actual table */
WHERE (
	COLUMN_NAME in ('deleted_when', 'date_deleted', 'last_seen','last_seen_date','date_last_seen')
	AND TABLE_NAME LIKE 'sqlwatch_meta%'
	)
OR
	(
	COLUMN_NAME in ('base_object_date_last_seen')
	AND TABLE_NAME = 'sqlwatch_config_check'
	);

set nocount on;

exec (@sql);
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
	CREATE PROCEDURE [dbo].[usp_sqlwatch_internal_query_to_html_table]
	(
	  @query nvarchar(MAX), 
	  @order_by nvarchar(MAX) = null, 
	  @html nvarchar(MAX) = null output 
	)

	as

	/* 
		If you are using custom css, please note the class name:
		<table class="sqlwatchtbl"
		-- based on https://stackoverflow.com/a/29708178
	*/

	begin   
		declare @sql nvarchar(max) = '',
				@error_message  nvarchar(max) = '',
				@thead nvarchar(max),
				@cols nvarchar(max),
				@tmp_table nvarchar(max),
				@error_message_single nvarchar(max) = '',
				@has_errors bit = 0
		
		set nocount on;

		set @tmp_table = '##'+replace(convert(varchar(max),newid()),'-','')
		set @order_by = case when @order_by is null then '' else replace(@order_by, '''', '''''') end

		set @sql = 'select * into ' + @tmp_table + ' from (' + @query + ') t;'

		begin try
			exec sp_executesql @sql 
		end try
		begin catch
			set @has_errors = 1

			set @error_message = 'Executing initial query'

			exec [dbo].[usp_sqlwatch_internal_log]
				@proc_id = @@PROCID,
				@process_stage = '77EF7172-3573-46B7-91E6-9BF0259B2DAC',
				@process_message = @error_message,
				@process_message_type = 'ERROR'
		end catch

		select @cols = coalesce(@cols + ', '''', ', '') + '[' + name + '] AS ''td'''
		from tempdb.sys.columns 
		where object_id = object_id('tempdb..' + @tmp_table)
		order by column_id;

		set @cols = 'set @html = cast(( select ' + @cols + ' from ' + @tmp_table + ' ' + @order_by + ' for xml path (''tr''), elements) as nvarchar(max))'    

		begin try
			exec sys.sp_executesql @cols, N'@html nvarchar(max) OUTPUT', @html=@html output
		end try
		begin catch

			set @has_errors = 1

			set @error_message = 'Building html content.'
			exec [dbo].[usp_sqlwatch_internal_log]
				@proc_id = @@PROCID,
				@process_stage = '52357550-B447-4352-9E0C-16353A967709',
				@process_message = @error_message,
				@process_message_type = 'ERROR'
		end catch

		select @thead = coalesce(@thead + '', '') + '<th>' + name + '</th>' 
		from tempdb.sys.columns 
		where object_id = object_id('tempdb..' + @tmp_table)
		order by column_id;

		set @thead = '<tr><thead>' + @thead + '</tr></thead>';
		set @html = '<table class="sqlwatchtbl">' + @thead + '<tbody>' + @html + '</tbody></table>';    

	if nullif(@error_message,'') is not null
		begin
			set @error_message = 'Errors during execution (' + OBJECT_NAME(@@PROCID) + ')'
			set @html = '<p style="color:red;">' + @error_message + '</p>'
			--print all errors but not terminate the batch as we are going to include this error instead of the report for the attention.
			raiserror ('%s',1,1,@error_message)
		end
	end
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_internal_restart_queues]
as

declare @sql varchar(max) = '';

-- disable all queues:
select @sql = @sql + 'ALTER QUEUE ' + name + ' WITH STATUS = OFF;' + char(10) 
from sys.service_queues
where name like 'sqlwatch%'

exec (@sql)

waitfor delay '00:00:05'

-- clean up all conversations:
-- Stop and clean all sqlwatch conversations in the database. 
-- Whilst this is not normally recommended as it may abrubtly stopped all conversations, it is safe here as we have stopped all queues above.
set @sql = ''
select @sql = @sql + '
end conversation ''' + convert(varchar(max),conversation_handle) + ''' WITH CLEANUP;'
from sys.conversation_endpoints
where far_service like 'sqlwatch%'
and state_desc <> 'CLOSED' -- these will be cleaned up by SQL Server

exec (@sql)

waitfor delay '00:00:05'

--restart queues
select @sql = @sql + 'ALTER QUEUE ' + name + ' WITH STATUS = ON;' + char(10) 
from sys.service_queues
where name like 'sqlwatch%'

exec (@sql)

waitfor delay '00:00:05'

--reseed timer queues
    declare @conversation_handle uniqueidentifier;

    begin dialog conversation @conversation_handle
        from service [sqlwatch_exec]
        to service N'sqlwatch_exec', N'current database'
        with encryption = off,
             RELATED_CONVERSATION_GROUP = 'B273076A-5D10-4527-909F-955707905890';
    
    --initial delay:
    begin conversation timer (@conversation_handle) timeout = 5;

    begin dialog conversation @conversation_handle
        from service [sqlwatch_exec]
        to service N'sqlwatch_exec', N'current database'
        with encryption = off,
             RELATED_CONVERSATION_GROUP = 'A2719CB0-D529-46D6-8EFE-44B44676B54B';

    --initial delay:
    begin conversation timer (@conversation_handle) timeout = 60;

    begin dialog conversation @conversation_handle
        from service [sqlwatch_exec]
        to service N'sqlwatch_exec', N'current database'
        with encryption = off,
             RELATED_CONVERSATION_GROUP = 'F65F11A7-25CF-4A4D-8A4F-C75B03FE083F';

    --initial delay:
    begin conversation timer (@conversation_handle) timeout = 70;

    begin dialog conversation @conversation_handle
        from service [sqlwatch_exec]
        to service N'sqlwatch_exec', N'current database'
        with encryption = off,
             RELATED_CONVERSATION_GROUP = 'E623DC39-A79D-4F51-AAAD-CF6A910DD72A';
    
    --initial delay:
    begin conversation timer (@conversation_handle) timeout = 90;
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_internal_retention]
as

set nocount on;
set xact_abort on;

declare @snapshot_type_id tinyint,
		@batch_size smallint,
		@row_count int,
		@action_queue_retention_days_failed smallint,
		@action_queue_retention_days_success smallint,
		@application_log_retention_days smallint,
		@sql_instance varchar(32) = dbo.ufn_sqlwatch_get_servername();

select @batch_size = [dbo].[ufn_sqlwatch_get_config_value](6, null)
select @action_queue_retention_days_failed = [dbo].[ufn_sqlwatch_get_config_value](3, null)
select @action_queue_retention_days_success = [dbo].[ufn_sqlwatch_get_config_value](4, null)
select @application_log_retention_days = [dbo].[ufn_sqlwatch_get_config_value](1, null)
select @row_count = 1 -- initalitzaion, otherwise loop will not be entered

declare @cutoff_dates as table (
	snapshot_time datetime2(0),
	sql_instance varchar(32),
	snapshot_type_id tinyint,
	primary key ([sql_instance], [snapshot_type_id])
)
	
/*	To account for central repository, we need a list of all possible snapshot types cross joined with servers list
	and calculate retention times from the type. This cannot be done for retention -1 as for that scenario, 
	we need to know the latest current snapshot.	*/
insert into @cutoff_dates
	select snapshot_time = case when st.snapshot_retention_days >0 then dateadd(day,-st.snapshot_retention_days,GETUTCDATE()) else null end
		, si.sql_instance
		, st.snapshot_type_id
	from [dbo].[sqlwatch_config_snapshot_type] st
	cross join [dbo].[sqlwatch_config_sql_instance] si

/*	Once we have a list of snapshots and dates, 
	we can get max snapshot for the rest - to avoid excesive scanning
	and try force a seek, we are limiting this to only those have not got date yet i.e. snapshot types = -1	*/
update c
	set snapshot_time = t.snapshot_time
from @cutoff_dates c
inner join (
	select snapshot_time=max(sh.snapshot_time), sh.sql_instance, sh.snapshot_type_id
	from dbo.sqlwatch_logger_snapshot_header sh
	inner join @cutoff_dates cd
		on cd.sql_instance = sh.sql_instance collate database_default
		and cd.snapshot_type_id = sh.snapshot_type_id
	where cd.snapshot_time is null
	group by sh.sql_instance, sh.snapshot_type_id
	) t
on t.sql_instance = c.sql_instance collate database_default
and t.snapshot_type_id = c.snapshot_type_id

while @row_count > 0
	begin
		begin tran
			delete top (@batch_size) h
			from dbo.[sqlwatch_logger_snapshot_header] h (readpast)
			inner join @cutoff_dates c 
				on h.snapshot_time < c.snapshot_time
				and h.sql_instance = c.sql_instance
				and h.snapshot_type_id = c.snapshot_type_id

			-- do not remove baseline snapshots:
			where h.snapshot_time not in (
				select snapshot_time
				from [dbo].[sqlwatch_meta_snapshot_header_baseline]
				where sql_instance = h.sql_instance
				)

			set @row_count = @@ROWCOUNT
			print 'Deleted ' + convert(varchar(max),@row_count) + ' records from [dbo].[sqlwatch_logger_snapshot_header]'
		commit tran
	end

	/*	delete old records from the action queue */
	delete 
	from [dbo].[sqlwatch_meta_action_queue] 
	where [time_queued] < case when exec_status <> 'FAILED' then dateadd(day,-@action_queue_retention_days_success,sysdatetime()) else dateadd(day,-@action_queue_retention_days_failed,sysdatetime()) end
	Print 'Deleted ' + convert(varchar(max),@@ROWCOUNT) + ' records from [dbo].[sqlwatch_meta_action_queue]'

	/* Application log retention */
set @row_count = 1
while @row_count > 0
	begin
		delete top (@batch_size)
		from dbo.sqlwatch_app_log
		where event_time < dateadd(day,-@application_log_retention_days, SYSDATETIME())

		set @row_count = @@ROWCOUNT
		Print 'Deleted ' + convert(varchar(max),@@ROWCOUNT) + ' records from [dbo].[sqlwatch_app_log]'
	end

	/*	Trend tables retention.
		These are detached from the header so we can keep more history and in a slightly different format to utilise less storage.
		We are going to have remove data from these tables manually	*/

	set @snapshot_type_id = 1 --Performance Counters
	delete from [dbo].[sqlwatch_trend_perf_os_performance_counters]
	where sql_instance = @sql_instance
	and getutcdate() > valid_until
	Print 'Deleted ' + convert(varchar(max),@@ROWCOUNT) + ' records from [dbo].[sqlwatch_trend_perf_os_performance_counters]'
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_internal_run_job]
	@job_name sysname,
	@fail_on_error bit = 1
as

set nocount on 

declare @job_id uniqueidentifier,
		@job_owner sysname

declare @xp_results table (
	job_id UNIQUEIDENTIFIER NOT NULL,
	last_run_date INT NOT NULL,
	last_run_time INT NOT NULL,
	next_run_date INT NOT NULL,
	next_run_time INT NOT NULL,
	next_run_schedule_id INT NOT NULL,
	requested_to_run INT NOT NULL, -- BOOL
	request_source INT NOT NULL,
	request_source_id sysname COLLATE database_default NULL,
	running INT NOT NULL, -- BOOL
	current_step INT NOT NULL,
	current_retry_attempt INT NOT NULL,
	job_state INT NOT NULL)

select @job_id = job_id, @job_owner = owner_sid 
from msdb.dbo.sysjobs where name = @job_name

insert into @xp_results
exec master.dbo.xp_sqlagent_enum_jobs 1, @job_owner, @job_id

if exists (select top 1 * FROM @xp_results where running = 1)
	begin
		--job is running, quit
		raiserror('Job ''%s'' is already running.',10, 1, @job_name)
        return
	end

declare @startime datetime2(7) = current_timestamp

exec msdb.dbo.sp_start_job @job_name = @job_name
waitfor delay '00:00:01' --without it we get incorrect results from enum_jobs as it does not register immedially

insert into @xp_results
exec master.dbo.xp_sqlagent_enum_jobs 1, @job_owner, @job_id

while exists (select * from @xp_results where running = 1)
	begin
		waitfor delay '00:00:00.500'
		delete from @xp_results
		insert into @xp_results
		exec master.dbo.xp_sqlagent_enum_jobs 1, @job_owner, @job_id
	end

if (select top 1 run_status 
	from msdb.dbo.sysjobhistory 
	where job_id = @job_id and step_id = 0 
	order by run_date desc, run_time desc) = 1 
	begin
		Print 'Job ''' + @job_name + ''' finished successfully in ' + convert(varchar(1000),datediff(millisecond,@startime,current_timestamp)) + 'ms.'
	end
else
	begin
		declare @msg nvarchar(512)
		set @msg = 'Job ' + quotename(@job_name) + ' has not finished successfuly or the state is not known. This does not necessarily mean that the job has failed.'
		if @fail_on_error = 1
			begin
				raiserror(@msg,16, 1, @job_name)
			end
		else
			begin
				print @msg
			end
	end
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_internal_start_xes]
	@force_start bit = 1
AS


/*
-------------------------------------------------------------------------------------------------------------------
 Procedure:
	usp_sqlwatch_config_start_xes

 Description:
	Start SQLWATCH extended event sessions. 
	Visual Studio has no way of starting up sessions post deployment.

 Parameters
	@force_start	by default we are only starting up SQLWATCH sessions on first deployment but if a user disables
					the session post deployment, we should never attempt to start it again.

 Author:
	Marcin Gminski

 Change Log:
	1.0		2019-01-15	Marcin Gminski, Initial version
-------------------------------------------------------------------------------------------------------------------
*/


if (select count(*) from dbo.sqlwatch_app_version) = 0 or @force_start = 1
	begin
		  declare @sql nvarchar(max) = ''
		  select @sql = @sql + 'ALTER EVENT SESSION ' + quotename(name)+ '
ON SERVER  
STATE = START;' + char(10) +
'Print ''Starting up XE Session: ' + name + ';''' + char(10) + char(10) 
		  from sys.server_event_sessions
		  where name like 'SQLWATCH%'
		  
		  --exclude any running sessions:
		  and name not in (
			select name
			from sys.dm_xe_sessions
			)

		exec (@sql)
	end
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_internal_tester]
	@test_name varchar(128)
AS

set nocount on;

-- simple procedure to replace Pester tests
-- since testing database involves running T-SQL against tables, there's not to involve PowrShell as we may as well write T-SQL in the procedure
-- this procedure will also serve as a "health check" to check that SQLWATCH is running OK.

declare @sql nvarchar(max) = ''

------------------------------------------------------------------------------------
-- test blocking
------------------------------------------------------------------------------------
if @test_name in ('All', 'Blocking') 
	begin
		--first, check that we have blocking threshold set:
		if (select convert(int,value_in_use) from sys.configurations where name = 'blocked process threshold (s)') > 15
			begin
				raiserror ('Blocking Process Threshold is not enabled',10,0) with nowait
				return
			end

		--we need a new test database as SQLWATCH db has RCSI enabled which prevents blocking
		raiserror ('Creating test database',10,0) with nowait
		set @sql = 'create database SQLWATCH_BLOCKING_TEST;'
		exec sp_executesql @sql;

		--set db snapshot to not RCIS in case model is set to RCSI:
		raiserror ('Setting new database options',10,0) with nowait
		set @sql = 'ALTER DATABASE [SQLWATCH_BLOCKING_TEST] SET READ_COMMITTED_SNAPSHOT OFF
		ALTER DATABASE [SQLWATCH_BLOCKING_TEST] SET RECOVERY SIMPLE ;'
		exec sp_executesql @sql;
		
		--create sample table:
		raiserror ('Creating sample tables',10,0) with nowait
		set @sql = 'create table [SQLWATCH_BLOCKING_TEST].dbo.sqlwatch_test_blocking (colA int);
		insert into [SQLWATCH_BLOCKING_TEST].dbo.sqlwatch_test_blocking (colA)
		values (1);'
		exec sp_executesql @sql;

		--blocking can only happen when another session tries to access blocked object.
		--sql cannot do this on its own in a single procedure:
		raiserror ('Runnig Blocking transaction for 45 seconds',10,0) with nowait
		raiserror ('Run this manually in separate sessions (SSMS tabs) to cause blocking: 
		select * from [SQLWATCH_BLOCKING_TEST].dbo.sqlwatch_test_blocking
		',10,1) with nowait

		--create blocking now:
		set @sql = '
		begin tran
		select * from [SQLWATCH_BLOCKING_TEST].dbo.sqlwatch_test_blocking with (tablock, holdlock, xlock)
		waitfor delay ''00:00:45''
		commit tran
		waitfor delay ''00:00:10'''
		exec sp_executesql @sql;

		--drop database:
		raiserror ('Cleaning up',10,0) with nowait
		EXEC msdb.dbo.sp_delete_database_backuphistory @database_name = N'SQLWATCH_BLOCKING_TEST'
		set @sql = 'USE [master];
		ALTER DATABASE [SQLWATCH_BLOCKING_TEST] SET  SINGLE_USER WITH ROLLBACK IMMEDIATE;
		DROP DATABASE [SQLWATCH_BLOCKING_TEST];'
		exec sp_executesql @sql;

	end
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_internal_update_xes_query_count]
	@session_name nvarchar(64),
	@execution_count bigint,
	@last_event_time datetime
AS
	--TODO TO DO CAN BE REMOVED IN vNEXT
	update [dbo].[sqlwatch_stage_xes_exec_count]
	set  execution_count = @execution_count
		, last_event_time = @last_event_time
	where session_name = @session_name
	option (keep plan);
RETURN 0
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_logger_agent_job_history]
AS


SET XACT_ABORT ON

declare @snapshot_time datetime
declare @snapshot_type_id smallint

set @snapshot_type_id = 16

exec [dbo].[usp_sqlwatch_internal_insert_header] 
	@snapshot_time_new = @snapshot_time OUTPUT,
	@snapshot_type_id = @snapshot_type_id

insert into [dbo].[sqlwatch_logger_agent_job_history] (sql_instance, sqlwatch_job_id, sqlwatch_job_step_id, sysjobhistory_instance_id, sysjobhistory_step_id,
	run_duration_s, run_date, run_status, snapshot_time, snapshot_type_id, [run_date_utc])
select sql_instance=@@SERVERNAME, mj.[sqlwatch_job_id], js.sqlwatch_job_step_id, instance_id, step_id,
 run_duration_s = ((jh.run_duration/10000*3600 + (jh.run_duration/100)%100*60 + run_duration%100 )),
 run_date = msdb.dbo.agent_datetime(jh.run_date, jh.run_time),
 jh.run_status,
 snapshot_time = @snapshot_time, 
 snapshot_type_id = @snapshot_type_id,
 [run_date_utc] = dateadd(minute,(datepart(TZOFFSET,SYSDATETIMEOFFSET()))*-1,msdb.dbo.agent_datetime(jh.run_date, jh.run_time))
from msdb.dbo.sysjobhistory jh

	inner join msdb.dbo.sysjobs sj
		on jh.job_id = sj.job_id

	inner join dbo.sqlwatch_meta_agent_job mj
		--avoid implicit conversion warning:
		on mj.job_name = convert(nvarchar(128),sj.name) collate database_default
		and mj.job_create_date = sj.date_created
		and mj.sql_instance = @@SERVERNAME

	inner join dbo.sqlwatch_meta_agent_job_step js
		on js.sql_instance = mj.sql_instance
		and js.[sqlwatch_job_id] = mj.[sqlwatch_job_id]
		--avoid implicit conversion warning:
		and js.step_name = convert(nvarchar(128),jh.step_name) collate database_default

	/* make sure we are only getting new records from msdb history 
	   need to check performance over long time !!! */
	left join [dbo].[sqlwatch_logger_agent_job_history] sh
		on sh.sql_instance = mj.sql_instance
		and sh.[sysjobhistory_instance_id] = jh.instance_id
	
	where sh.[sysjobhistory_instance_id] is null
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_logger_disk_utilisation]
	@databases varchar(max) = 'ALL',
	@ignore_global_exclusion bit = 0
AS

/*
-------------------------------------------------------------------------------------------------------------------
 Procedure:
	[usp_sqlwatch_logger_disk_utilisation]

 Description:
	Collect Disk utilisation.

 Parameters
	
 Author:
	Marcin Gminski

 Change Log:
	1.0		2018-08		- Marcin Gminski, Initial version
	1.1		2020-03-18	- Marcin Gminski, move explicit transaction after header to fix https://github.com/marcingminski/sqlwatch/issues/155
	1.2		2020-03-22	- Marcin Gminski, moved off sp_MSforeachdb
	1.3		2020-05-16  - Marcin Gminski, https://github.com/marcingminski/sqlwatch/issues/165. 
			NOTES: The [dbo].[usp_sqlwatch_internal_foreachdb] could simply execute a SQL that inserts directly into 
			the destination table [dbo].[sqlwatch_logger_disk_utilisation_database]. There is room for improevemnt here.
-------------------------------------------------------------------------------------------------------------------
*/

set nocount on;

set xact_abort on


declare @snapshot_type_id tinyint = 2,
		@snapshot_time datetime2(0),
		@product_version nvarchar(128),
		@product_version_major decimal(10,2),
		@product_version_minor decimal(10,2),
		@sql varchar(max)

select @product_version = convert(nvarchar(128),serverproperty('productversion'));
select @product_version_major = substring(@product_version, 1,charindex('.', @product_version) + 1 )
	  ,@product_version_minor = parsename(convert(varchar(32), @product_version), 2);

--------------------------------------------------------------------------------------------------------------
-- get new header
--------------------------------------------------------------------------------------------------------------
exec [dbo].[usp_sqlwatch_internal_insert_header] 
	@snapshot_time_new = @snapshot_time OUTPUT,
	@snapshot_type_id = @snapshot_type_id

--------------------------------------------------------------------------------------------------------------
-- get sp_spaceused
--------------------------------------------------------------------------------------------------------------
declare @spaceused table (
		[database_name] nvarchar(128),
		[database_size] varchar(18),
		[unallocated_space] varchar(18),
		[reserved] varchar(18),
		[data] varchar(18),
		[index_size] varchar(18),
		[unused] varchar(18)
)

--https://github.com/marcingminski/sqlwatch/issues/165
declare @spaceused_extent table (
	[database_name] nvarchar(128),
	unallocated_extent_page_count bigint,
	allocated_extent_page_count bigint,
	version_store_reserved_page_count bigint,
	user_object_reserved_page_count bigint,
	internal_object_reserved_page_count bigint,
	mixed_extent_page_count bigint,
	unique clustered ([database_name]) 
)

insert into @spaceused_extent
exec [dbo].[usp_sqlwatch_internal_foreachdb] 
	@snapshot_type_id = @snapshot_type_id,
	@calling_proc_id = @@PROCID,
	@databases = @databases,
	@command =  'USE [?];
select 
	 DB_NAME()
	,sum(a.unallocated_extent_page_count) 
    ,sum(a.allocated_extent_page_count) 
    ,sum(a.version_store_reserved_page_count) 
    ,sum(a.user_object_reserved_page_count) 
    ,sum(a.internal_object_reserved_page_count) 
    ,sum(a.mixed_extent_page_count)
from sys.dm_db_file_space_usage a'


if @product_version_major >= 13
/*	since SQL 2016 Microsoft have improved sp_spaceused which now returns one recordset making it easier
	to insert into tables */
	begin
		insert into @spaceused
			exec [dbo].[usp_sqlwatch_internal_foreachdb] @command = 'use [?]; exec sp_spaceused @oneresultset = 1;'
				, @snapshot_type_id = @snapshot_type_id
				, @calling_proc_id = @@PROCID
				, @databases = @databases
	end
else
	begin
	/*	pre 2016 however is not all that easy. sp_spaceused will return multiple resultsets making it impossible
		to insert into a table. The below is more or less what sp_spaceused is doing */
		insert into @spaceused
		exec [dbo].[usp_sqlwatch_internal_foreachdb] 
			@snapshot_type_id = @snapshot_type_id,
			@calling_proc_id = @@PROCID,
			@databases = @databases,
			@command =  'USE [?];
		declare  @id	int			
				,@type	character(2) 
				,@pages	bigint
				,@dbname sysname
				,@dbsize bigint
				,@logsize bigint
				,@reservedpages  bigint
				,@usedpages  bigint
				,@rowCount bigint

			select 
				  @dbsize = sum(convert(bigint,case when status & 64 = 0 then size else 0 end))
				, @logsize = sum(convert(bigint,case when status & 64 <> 0 then size else 0 end))
				from dbo.sysfiles

			select 
				@reservedpages = sum(a.total_pages),
				@usedpages = sum(a.used_pages),
				@pages = sum(
						case
							-- XML-Index and FT-Index and semantic index internal tables are not considered "data", but is part of "index_size"
							when it.internal_type IN (202,204,207,211,212,213,214,215,216,221,222,236) then 0
							when a.type <> 1 and p.index_id < 2 then a.used_pages
							when p.index_id < 2 then a.data_pages
							else 0
						end
					)
			from sys.partitions p join sys.allocation_units a on p.partition_id = a.container_id
				left join sys.internal_tables it on p.object_id = it.object_id

			select 
				database_name = db_name(),
				database_size = ltrim(str((convert (dec (15,2),@dbsize) + convert (dec (15,2),@logsize)) 
					* 8192 / 1048576,15,2) + '' MB''),
				''unallocated space'' = ltrim(str((case when @dbsize >= @reservedpages then
					(convert (dec (15,2),@dbsize) - convert (dec (15,2),@reservedpages)) 
					* 8192 / 1048576 else 0 end),15,2) + '' MB''),
				reserved = ltrim(str(@reservedpages * 8192 / 1024.,15,0) + '' KB''),
				data = ltrim(str(@pages * 8192 / 1024.,15,0) + '' KB''),
				index_size = ltrim(str((@usedpages - @pages) * 8192 / 1024.,15,0) + '' KB''),
				unused = ltrim(str((@reservedpages - @usedpages) * 8192 / 1024.,15,0) + '' KB'')
				'
	end

--------------------------------------------------------------------------------------------------------------
-- get log usage
--------------------------------------------------------------------------------------------------------------
declare @logspace_SQL2008 table (
	[database_name] sysname,
	[log_space_mb] decimal(18,2),
	[log_space_used_perc] real,
	[status] bit
)

declare @logspace table (
	[database_name] sysname,
	[total_log_size_in_bytes] bigint,
	[used_log_space_in_bytes] bigint
)

if @product_version_major < 11
	begin
		insert into @logspace_SQL2008
			exec ('DBCC SQLPERF(LOGSPACE);')

		/* make into a 2012 format */
		insert into @logspace
		select 
			[database_name],
			[total_log_size_in_bytes] = [log_space_mb] * 1024.0 * 1024.0,
			[used_log_space_in_bytes] = ([log_space_mb] * [log_space_used_perc] / 100.0) * 1024.0 * 1024.0
		from @logspace_SQL2008
	end
else
	begin
		--https://github.com/marcingminski/sqlwatch/issues/90
		--https://support.microsoft.com/en-gb/help/4088901/fix-assertion-failure-for-sys-dm-db-log-space-usage-on-database
		--exclude log collection for database snapshots. Snapshots have no logs anyway.
		insert into @logspace
			exec [dbo].[usp_sqlwatch_internal_foreachdb] 
				@snapshot_type_id = @snapshot_type_id,
				@calling_proc_id = @@PROCID,		
				@databases = @databases,
				@command =  '
				use [?]
				if exists (select 1 from sys.databases where name = ''?'' 
							and source_database_id is null)
					begin
						select 
							''?'',
							[total_log_size_in_bytes],
							[used_log_space_in_bytes]
						from sys.dm_db_log_space_usage
					end'
	end


--------------------------------------------------------------------------------------------------------------
-- combine results and insert into the table
--------------------------------------------------------------------------------------------------------------
begin tran
	insert into [dbo].[sqlwatch_logger_disk_utilisation_database]
	select 
		--  su.[database_name]
		--, [database_create_date] = db.create_date
		[sqlwatch_database_id] = swd.[sqlwatch_database_id]
		/*	
			conversion from sp_spaceused MiB format to bytes so we have consistent units 
			to test that this gives us an exact number:
			sp_spaceused returns 7.63 MB for master database.
			our conversion below gives us 8000634 bytes -> covnert back to MB: 
				8000634 / 1024 / 1024 = 7.6299 MB
			Try: http://www.wolframalpha.com/input/?i=8000634+bytes+in+MiB 
		*/
		, [database_size_bytes] = convert(bigint,convert(decimal(19,2),replace([database_size],' MB','')) * 1024 * 1024)
		, [unallocated_space_bytes] = convert(bigint,convert(decimal(19,2),replace([unallocated_space],' MB','')) * 1024.0 * 1024.0)
		, [reserved_bytes] = convert(bigint,convert(decimal(19,2),replace([reserved],' KB','')) * 1024.0)
		, [data_bytes] = convert(bigint,convert(decimal(19,2),replace([data],' KB','')) * 1024.0)
		, [index_size_bytes] = convert(bigint,convert(decimal(19,2),replace([index_size],' KB','')) * 1024.0)
		, [unused_bytes] = convert(bigint,convert(decimal(19,2),replace([unused],' KB','')) * 1024.0)
		, ls.[total_log_size_in_bytes]
		, ls.[used_log_space_in_bytes]
		, [snapshot_time] = @snapshot_time
		, [snapshot_type_id] = @snapshot_type_id
		, @@SERVERNAME

		, [unallocated_extent_page_count] = suex.[unallocated_extent_page_count]
		, [allocated_extent_page_count] = suex.[allocated_extent_page_count]
		, [version_store_reserved_page_count] = suex.[version_store_reserved_page_count]
		, [user_object_reserved_page_count] = suex.[user_object_reserved_page_count]
		, [internal_object_reserved_page_count] = suex.[internal_object_reserved_page_count]
		, [mixed_extent_page_count] = suex.[mixed_extent_page_count]

	from @spaceused su
	inner join @logspace ls
		on su.[database_name] = ls.[database_name] collate database_default
	inner join vw_sqlwatch_sys_databases db
		on db.[name] = su.[database_name] collate database_default
	/*	join on sqlwatch database list otherwise it will fail
		for newly created databases not yet added to the list */
	inner join [dbo].[sqlwatch_meta_database] swd
		on swd.[database_name] = db.[name] collate database_default
		and swd.[database_create_date] = db.[create_date]
		and swd.sql_instance = @@SERVERNAME

	left join @spaceused_extent suex
		on su.[database_name] = suex.[database_name] collate database_default

	left join [dbo].[sqlwatch_config_exclude_database] ed
		on swd.[database_name] like ed.database_name_pattern
		and ed.snapshot_type_id = @snapshot_type_id

	where ed.snapshot_type_id is null

commit tran
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_logger_disk_utilisation_os_volume]
	@volume_name nvarchar(255),
	@volume_free_space_bytes bigint,
	@volume_total_space_bytes bigint
as

declare @snapshot_type_id smallint = 17,
		@snapshot_time datetime2(0);

exec [dbo].[usp_sqlwatch_internal_insert_header] 
	@snapshot_time_new = @snapshot_time OUTPUT,
	@snapshot_type_id = @snapshot_type_id;
		
insert into [dbo].[sqlwatch_logger_disk_utilisation_volume] (
	[sqlwatch_volume_id],
	[volume_free_space_bytes],
	[volume_total_space_bytes],
	[snapshot_time],
	[snapshot_type_id],
	[sql_instance])

select [sqlwatch_volume_id],
	[volume_free_space_bytes] = @volume_free_space_bytes,
	[volume_total_space_bytes] = @volume_total_space_bytes,
	[snapshot_time] = @snapshot_time,
	[snapshot_type_id] = @snapshot_type_id,
	[sql_instance] = ov.[sql_instance]
from [dbo].[sqlwatch_meta_os_volume] ov
where volume_name = @volume_name
and sql_instance = @@SERVERNAME
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_logger_disk_utilisation_table]
	@debug bit = 0,
	@databases varchar(max) = '-tempdb',
	@ignore_global_exclusion bit = 0
as

declare @sql nvarchar(max),
		@sqlwatchdb nvarchar(128) = DB_NAME(),
		@snapshot_type_id tinyint = 22,
		@snapshot_time datetime2(0),
		@previous_snapshot_time datetime2(0)

	select @previous_snapshot_time = max(snapshot_time)
	from dbo.sqlwatch_logger_snapshot_header
	where sql_instance = @@SERVERNAME
	and snapshot_type_id = @snapshot_type_id

	exec [dbo].[usp_sqlwatch_internal_insert_header] 
		@snapshot_time_new = @snapshot_time OUTPUT,
		@snapshot_type_id = @snapshot_type_id

set @sql = '
set transaction isolation level read uncommitted
declare @tablecount bigint,
		@process_message nvarchar(max)

select @tablecount = count(*) from [?].INFORMATION_SCHEMA.TABLES where TABLE_TYPE = ''BASE TABLE''
set @process_message = ''Collecting table size for database [?]. Total tables: '' + convert(varchar(10),@tablecount) + ''.''

exec [dbo].[usp_sqlwatch_internal_log]
	@proc_id = ' + convert(varchar(10),@@PROCID) + ',
	@process_stage = ''5A046B12-0CF5-4D14-8777-48AAEC8CAA70'',
	@process_message = @process_message,
	@process_message_type = ''INFO'';

declare @t table (
	schema_name sysname,
	table_name sysname,
	database_name sysname,
	database_create_date datetime,
	row_count real,
	total_pages real,
	used_pages real,
	data_compression bit,
	unique clustered (schema_name, table_name) 
)

insert into @t
select 
	schema_name = s.name,
	table_name = t.name, 
	database_name = sdb.name,
	database_create_date = sdb.create_date,
	row_count = convert(real,sum(p.rows)),
	total_pages = convert(real,sum(a.total_pages)),
	used_pages = convert(real,sum(a.used_pages)),
	/* only take table compression into account and not index compression.
	   we have index analysis elsewhere */
	[data_compression] = max(case when i.index_id = 0 then p.[data_compression] else 0 end)
from [?].sys.tables t
inner join [?].sys.indexes i on t.object_id = i.object_id
inner join [?].sys.partitions p on i.object_id = p.object_id AND i.index_id = p.index_id
inner join [?].sys.allocation_units a on p.partition_id = a.container_id
inner join [?].sys.schemas s on t.schema_id = s.schema_id
inner join sys.databases sdb on sdb.name = ''?''

group by s.name, t.name, sdb.name, sdb.create_date;

insert into ' + quotename(@sqlwatchdb) + '.[dbo].[sqlwatch_logger_disk_utilisation_table](
	  sqlwatch_database_id
	, sqlwatch_table_id
	, row_count
	, total_pages
	, used_pages
	, data_compression
	, snapshot_type_id
	, snapshot_time
	, sql_instance
	, row_count_delta
	, total_pages_delta
	, used_pages_delta
	)
select 
	mt.sqlwatch_database_id,
	mt.sqlwatch_table_id,
	t.row_count,
	t.total_pages,
	t.used_pages,
	t.[data_compression],
	' + convert(varchar(10),@snapshot_type_id) + ',
	''' + convert(varchar(23),@snapshot_time,121) + ''',
	@@SERVERNAME,
	row_count_delta = convert(real,isnull(t.row_count - dt.row_count,0)),
	total_pages_delta = convert(real,isnull(t.total_pages - dt.total_pages,0)),
	used_pages_delta = convert(real,isnull(t.used_pages - dt.used_pages,0))
from @t t

inner join ' + quotename(@sqlwatchdb) + '.[dbo].[sqlwatch_meta_database] mdb
	on mdb.database_name = t.database_name collate database_default
	and mdb.database_create_date = t.database_create_date
	and mdb.sql_instance = @@SERVERNAME

inner join ' + quotename(@sqlwatchdb) + '.[dbo].[sqlwatch_meta_table] mt
	on mt.table_name = t.schema_name + ''.'' + t.table_name collate database_default
	and mt.sqlwatch_database_id = mdb.sqlwatch_database_id
	and mt.sql_instance = mdb.sql_instance

left join ' + quotename(@sqlwatchdb) + '.[dbo].[sqlwatch_logger_disk_utilisation_table] dt
	on dt.sqlwatch_database_id = mdb.sqlwatch_database_id
	and dt.sql_instance = mdb.sql_instance
	and dt.sqlwatch_table_id = mt.sqlwatch_table_id
	and dt.snapshot_time = ''' + convert(varchar(23),@previous_snapshot_time,121) + ''''

exec [dbo].[usp_sqlwatch_internal_foreachdb] 
		@command = @sql
	,	@snapshot_type_id = @snapshot_type_id
	,	@debug = @debug
	,	@calling_proc_id = @@PROCID
	,	@databases = @databases
	,	@ignore_global_exclusion = @ignore_global_exclusion
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_logger_errorlog]
AS

set nocount on ;
set xact_abort on;

CREATE TABLE #sqlwatch_logger_errorlog (
	log_date datetime,
	attribute_value varchar(max),
	text nvarchar(max),
	keyword_id smallint,
	log_type_id int
)

declare @keyword_id smallint, 
		@keyword1 nvarchar(255), 
		@keyword2 nvarchar(255), 
		@log_type_id int,
		@prev_log_date datetime,
		@snapshot_type_id tinyint = 25,
		@snapshot_time datetime2(0);

merge dbo.sqlwatch_meta_errorlog_keyword as target
using dbo.sqlwatch_config_include_errorlog_keywords as source
on	target.keyword_id = source.keyword_id
and target.log_type_id = source.log_type_id
and target.sql_instance = @@SERVERNAME

when not matched then
	insert (keyword_id , sql_instance, keyword1, keyword2, log_type_id)
	values (source.keyword_id, @@SERVERNAME, source.keyword1, source.keyword2, source.log_type_id);

declare c_parse_errorlog cursor for
select m.keyword_id, m.keyword1, m.keyword2, m.log_type_id 
from dbo.sqlwatch_meta_errorlog_keyword m
where m.sql_instance = @@SERVERNAME;

open c_parse_errorlog

fetch next from c_parse_errorlog into @keyword_id, @keyword1, @keyword2, @log_type_id;

while @@FETCH_STATUS = 0
	begin
		select @prev_log_date = dateadd(ms,3,log_date)
		from dbo.sqlwatch_logger_errorlog l
		where log_type_id = @log_type_id
		and keyword_id = @keyword_id
		and sql_instance = @@SERVERNAME;

		set @prev_log_date = isnull(dateadd(ms,3,@prev_log_date),'1970-01-01');

		insert into #sqlwatch_logger_errorlog (log_date,attribute_value,text)
		exec xp_readerrorlog 0, @log_type_id, @keyword1, @keyword2 ,@prev_log_date;

		update #sqlwatch_logger_errorlog
			set text = replace(replace(rtrim(ltrim(text)),char(13),''),char(10),'')
			, keyword_id = @keyword_id
			, log_type_id = @log_type_id
		where keyword_id is null and log_type_id is null;

	fetch next from c_parse_errorlog into @keyword_id, @keyword1, @keyword2, @log_type_id;
	end

close c_parse_errorlog;
deallocate c_parse_errorlog;

merge dbo.sqlwatch_meta_errorlog_attribute as target
using (
	select distinct sql_instance = @@SERVERNAME, attribute_name = case s.log_type_id
			when 1 then 'ProcessInfo'
			when 2 then 'ErrorLevel'
			else '' end
			, s.attribute_value
	from #sqlwatch_logger_errorlog s
	) as source

on target.sql_instance = source.sql_instance collate database_default
and target.attribute_name = source.attribute_name collate database_default
and target.attribute_value = source.attribute_value collate database_default
		
when not matched then
	insert (sql_instance, attribute_name, attribute_value)
	values (source.sql_instance, source.attribute_name, source.attribute_value)
;

insert into dbo.sqlwatch_meta_errorlog_text (errorlog_text)
select distinct text
from #sqlwatch_logger_errorlog s
left join dbo.sqlwatch_meta_errorlog_text t
	on t.errorlog_text = s.text collate database_default
where t.errorlog_text_id is null;

update t
	set total_occurence_count = isnull(t.total_occurence_count,0) + isnull(c.total_occurence_count,0)
	, last_occurence = case when c.last_occurence is not null then c.last_occurence else t.last_occurence end
	, first_occurence = case when c.first_occurence is not null then c.first_occurence else t.first_occurence end
from dbo.sqlwatch_meta_errorlog_text t
left join (
	select text, total_occurence_count=count(*), first_occurence=min(log_date), last_occurence=max(log_date)
	from #sqlwatch_logger_errorlog
	group by text
) c
on c.text = t.errorlog_text collate database_default
and sql_instance = @@SERVERNAME;

exec [dbo].[usp_sqlwatch_internal_insert_header] 
	@snapshot_time_new = @snapshot_time OUTPUT,
	@snapshot_type_id = @snapshot_type_id;

insert into dbo.sqlwatch_logger_errorlog (log_date, attribute_id, errorlog_text_id, keyword_id, log_type_id, snapshot_time, snapshot_type_id, record_count)
select log_date, attribute_id , t.errorlog_text_id, s.keyword_id, s.log_type_id, @snapshot_time, @snapshot_type_id, record_count=count(*)
from #sqlwatch_logger_errorlog s
left join dbo.sqlwatch_meta_errorlog_attribute a
on s.attribute_value = a.attribute_value collate database_default
and a.attribute_name = case s.log_type_id
		when 1 then 'ProcessInfo'
		when 2 then 'ErrorLevel'
		else null end 
left join dbo.sqlwatch_meta_errorlog_text t
	on t.errorlog_text = s.text collate database_default
group by log_date, attribute_id , t.errorlog_text_id, s.keyword_id, s.log_type_id;
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_logger_hadr_database_replica_states]
AS

declare @snapshot_type_id tinyint = 29
declare @date_snapshot_current datetime2(0)

--get snapshot header
exec [dbo].[usp_sqlwatch_internal_insert_header] 
	@snapshot_time_new = @date_snapshot_current OUTPUT,
	@snapshot_type_id = @snapshot_type_id

insert into [dbo].[sqlwatch_logger_hadr_database_replica_states]
(
	[hadr_group_name] ,
	[replica_server_name] ,
	[availability_mode] ,
	[failover_mode] ,
	[database_name],
	--[sqlwatch_database_id] ,
	[is_local] ,
	[is_primary_replica] ,
	[synchronization_state] ,
	[is_commit_participant] ,
	[synchronization_health] ,
	[database_state] ,
	[is_suspended] ,
	[suspend_reason] ,
	[log_send_queue_size] ,
	[log_send_rate] ,
	[redo_queue_size] ,
	[redo_rate] ,
	[filestream_send_rate] ,
	[secondary_lag_seconds] ,
	[last_commit_time] ,
	[snapshot_type_id] ,
	[snapshot_time] ,
	[sql_instance] 
	)

select 
	 hadr_group_name = ag.name
	,ar.replica_server_name
	,ar.availability_mode
	,ar.failover_mode
	--,db.sqlwatch_database_id
	,[database_name] = dbs.name
	,rs.is_local
	,[is_primary_replica] = null --rs.[is_primary_replica] --2014 onwards
	,rs.[synchronization_state]
	,rs.[is_commit_participant]
	,rs.[synchronization_health]
	,rs.[database_state]
	,rs.[is_suspended]
	,rs.[suspend_reason]
	,rs.[log_send_queue_size]
	,rs.[log_send_rate]
	,rs.[redo_queue_size]
	,rs.[redo_rate]
	,rs.[filestream_send_rate]
	,[secondary_lag_seconds] = null --rs.[secondary_lag_seconds] --2014 onwards
	,rs.[last_commit_time]
	,[snapshot_type_id]=@snapshot_type_id
	,[snapshot_time]=@date_snapshot_current
	,[sql_instance]=[dbo].[ufn_sqlwatch_get_servername]()
from sys.dm_hadr_database_replica_states rs
inner join sys.availability_replicas ar 
	on ar.group_id = rs.group_id
	and ar.replica_id = rs.replica_id
inner join sys.availability_groups ag
	on ag.group_id = rs.group_id
inner join sys.databases dbs
	on dbs.database_id = rs.database_id
--inner join dbo.vw_sqlwatch_sys_databases sdb
--	on sdb.database_id = rs.database_id
--inner join dbo.sqlwatch_meta_database db
--	on db.database_name = sdb.name
--	and db.database_create_date = sdb.create_date
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_logger_index_histogram]

as

/*
-------------------------------------------------------------------------------------------------------------------
 Procedure:
	[usp_sqlwatch_logger_index_histogram]

 Description:
	Collect index histogram.

 Parameters
	
 Author:
	Marcin Gminski

 Change Log:
	1.0		2018-08		- Marcin Gminski, Initial version
	1.1		2020-03-18	- Marcin Gminski, move explicit transaction after header to fix https://github.com/marcingminski/sqlwatch/issues/155
-------------------------------------------------------------------------------------------------------------------
*/

set xact_abort on
set nocount on

declare @snapshot_type_id tinyint = 14,
		@snapshot_time datetime,
		@database_name sysname,
		@sql varchar(max),
		@object_id int,
		@index_name sysname,
		@index_id int,
		@object_name nvarchar(256),
		@sqlwatch_database_id smallint,
		@sqlwatch_table_id int,
		@sqlwatch_index_id int

declare @indextype as table (
	is_index_hierarchical bit,
	is_index_timestamp bit
)


create table #stats_hierarchical (
	[database_name] sysname default 'fe92qw0fa_dummy',
	[object_name] sysname default 'fe92qw0fa_dummy',
	index_name sysname default 'fe92qw0fa_dummy',
	index_id int,
	RANGE_HI_KEY hierarchyid,
	RANGE_ROWS real,
	EQ_ROWS real,
	DISTINCT_RANGE_ROWS real,
	AVG_RANGE_ROWS real,
	[collection_time] datetime,
	[sqlwatch_database_id] smallint,
	[sqlwatch_table_id] int,
	[sqlwatch_index_id] int
)

/* new temp table because of  https://github.com/marcingminski/sqlwatch/issues/119 */ 
create table #stats_timestamp (
	[database_name] sysname default 'fe92qw0fa_dummy',
	[object_name] sysname default 'fe92qw0fa_dummy',
	index_name sysname default 'fe92qw0fa_dummy',
	index_id int,
	/*
		timestamp is a rowversion column - a binary "counter" to identify that the row has been modified. 
		it is unlikely to have index or/and stats on the rowversion column but we have seen it happen. (yay vendor apps!)
		so we have to be able to handle it. 		
		Anyway, timestamp (aka rowversion) will implicitly convert to varchar and datetime.
		when converted to varchar the value will be empty string, and when converted to datetime it will simply add the counter value to 1900-01-01
		and will show relatively random date. I don't either will be of any use and I'd be actually tempted to just not collect any stats from indexes on these columns 
		but happy to wait for community advice and expertise. 
	*/
	RANGE_HI_KEY datetime, 
	RANGE_ROWS real,
	EQ_ROWS real,
	DISTINCT_RANGE_ROWS real,
	AVG_RANGE_ROWS real,
	[collection_time] datetime,
	[sqlwatch_database_id] smallint,
	[sqlwatch_table_id] int,
	[sqlwatch_index_id] int
)

create table #stats (
	[database_name] sysname default 'fe92qw0fa_dummy',
	[object_name] sysname default 'fe92qw0fa_dummy',
	index_name sysname default 'fe92qw0fa_dummy',
	index_id int,
	RANGE_HI_KEY sql_variant,
	RANGE_ROWS real,
	EQ_ROWS real,
	DISTINCT_RANGE_ROWS real,
	AVG_RANGE_ROWS real,
	[collection_time] datetime,
	[sqlwatch_database_id] smallint,
	[sqlwatch_table_id] int,
	[sqlwatch_index_id] int
)

declare @is_index_hierarchical bit
declare @is_index_timestamp bit  

set  @snapshot_type_id = 15

declare c_index cursor for
select md.[database_name], table_name=mt.table_name , index_name = mi.index_name, mi.index_id, mi.sqlwatch_database_id, mi.sqlwatch_table_id, mi.sqlwatch_index_id
from [dbo].[sqlwatch_meta_index] mi

	inner join [dbo].[sqlwatch_meta_table] mt
		on mt.sqlwatch_database_id = mi.sqlwatch_database_id
		and mt.sql_instance = mi.sql_instance
		and mt.sqlwatch_table_id = mi.sqlwatch_table_id

	inner join [dbo].[sqlwatch_meta_database] md
		on md.sql_instance = mi.sql_instance
		and md.sqlwatch_database_id = mi.sqlwatch_database_id

	inner join [dbo].[vw_sqlwatch_sys_databases] sdb
		on sdb.name = md.database_name collate database_default
		and sdb.create_date = md.database_create_date

	/*	Index histograms can be very large and since its only required for a very specific performance tuning, 
		we are only going to collect those exclusively included for collection	*/
	inner join [dbo].[sqlwatch_config_include_index_histogram] ih
		on md.[database_name] like parsename(ih.object_name_pattern,3)
		and mt.table_name like parsename(ih.object_name_pattern,2) + '.' + parsename(ih.object_name_pattern,1)
		and mi.index_name like ih.index_name_pattern

	left join [dbo].[sqlwatch_config_exclude_database] ed
		on md.[database_name] like ed.database_name_pattern
		and ed.snapshot_type_id = @snapshot_type_id

	where ed.snapshot_type_id is null

open c_index

fetch next from c_index
into @database_name, @object_name, @index_name, @index_id, @sqlwatch_database_id, @sqlwatch_table_id, @sqlwatch_index_id

while @@FETCH_STATUS = 0
	begin
		delete from @indextype
		select @is_index_hierarchical = 0, @is_index_timestamp = 0

		set @sql = 'use [' + @database_name + ']; 
			select
				is_index_hierarchical = case when tp.name = ''hierarchyid'' then 1 else 0 end,
				is_index_timestamp = case when tp.name = ''timestamp'' then 1 else 0 end
			from sys.schemas s
			inner join sys.tables t 
				on s.schema_id = t.schema_id
			inner join sys.indexes i 
				on i.object_id = t.object_id
			inner join sys.index_columns ic 
				on ic.index_id = i.index_id 
				and ic.object_id = i.object_id
				/* only the leading column is used to build histogram 
				   https://dba.stackexchange.com/a/182250 */
				and ic.index_column_id = 1
			inner join sys.columns c 
				on c.column_id = ic.column_id 
				and c.object_id = ic.object_id
			inner join sys.types tp
				on tp.system_type_id = c.system_type_id
				and tp.user_type_id = c.user_type_id
			where i.name = ''' + @index_name + '''
			and s.name + ''.'' + t.name = ''' + @object_name + ''''
		insert into @indextype(is_index_hierarchical, is_index_timestamp)
		exec (@sql)

		select 
			@is_index_hierarchical = is_index_hierarchical,
			@is_index_timestamp = is_index_timestamp
		from @indextype


		--set @object_name = object_schema_name(@object_id) + '.' + object_name(@object_id)
		set @sql = 'use [' + @database_name + ']; 
--extra check if the table and index still exist. since we are collecting histogram for indexes already collected in sqlwatch,
--there could be a situation where index was deleted from Sql Server before SQLWATCH was upated and the below would have thrown an error.
if exists (
		select *
		from sys.indexes 
		where object_id = object_id(''' + @object_name + ''')
		and name=''' + @index_name + ''')
	begin
		dbcc show_statistics (''' + @object_name + ''',''' + @index_name + ''') with  HISTOGRAM
		Print ''['' + convert(varchar(23),getdate(),121) + ''] Collecting index histogram for index: ' + @index_name + '''
	end'

		if @is_index_hierarchical = 1
			begin
				insert into #stats_hierarchical (RANGE_HI_KEY,RANGE_ROWS,EQ_ROWS,DISTINCT_RANGE_ROWS,AVG_RANGE_ROWS)
				exec (@sql)

				update #stats_hierarchical
					set [database_name] = @database_name
						, [object_name] = @object_name
						, index_name = @index_name
						, index_id = @index_id
						, [collection_time] = getutcdate()
						, [sqlwatch_database_id] = @sqlwatch_database_id
						, [sqlwatch_table_id] = @sqlwatch_table_id
						, [sqlwatch_index_id] = @sqlwatch_index_id
				where index_name = 'fe92qw0fa_dummy'
			end
		else if @is_index_timestamp = 1
			begin
				insert into #stats_timestamp (RANGE_HI_KEY,RANGE_ROWS,EQ_ROWS,DISTINCT_RANGE_ROWS,AVG_RANGE_ROWS)
				exec (@sql)

				update #stats_timestamp
					set [database_name] = @database_name
						, [object_name] = @object_name
						, index_name = @index_name
						, index_id = @index_id
						, [collection_time] = getutcdate()
						, [sqlwatch_database_id] = @sqlwatch_database_id
						, [sqlwatch_table_id] = @sqlwatch_table_id
						, [sqlwatch_index_id] = @sqlwatch_index_id
				where index_name = 'fe92qw0fa_dummy'
			end
		else
			begin
				insert into #stats (RANGE_HI_KEY,RANGE_ROWS,EQ_ROWS,DISTINCT_RANGE_ROWS,AVG_RANGE_ROWS)
				exec (@sql)

				update #stats
					set [database_name] = @database_name
						, [object_name] = @object_name
						, index_name = @index_name
						, index_id = @index_id
						, [collection_time] = getutcdate()
						, [sqlwatch_database_id] = @sqlwatch_database_id
						, [sqlwatch_table_id] = @sqlwatch_table_id
						, [sqlwatch_index_id] = @sqlwatch_index_id
				where index_name = 'fe92qw0fa_dummy'
			end

		fetch next from c_index
		into @database_name, @object_name, @index_name, @index_id, @sqlwatch_database_id, @sqlwatch_table_id, @sqlwatch_index_id
	end

close c_index
deallocate c_index 


	exec [dbo].[usp_sqlwatch_internal_insert_header] 
		@snapshot_time_new = @snapshot_time OUTPUT,
		@snapshot_type_id = @snapshot_type_id

begin tran

	insert into [dbo].[sqlwatch_logger_index_histogram](
		[sqlwatch_database_id], [sqlwatch_table_id], [sqlwatch_index_id],
		RANGE_HI_KEY, RANGE_ROWS, EQ_ROWS, DISTINCT_RANGE_ROWS, AVG_RANGE_ROWS,
		[snapshot_time], [snapshot_type_id], [collection_time])
	select
		st.[sqlwatch_database_id],
		st.[sqlwatch_table_id],
		st.[sqlwatch_index_id],
		convert(nvarchar(max),st.RANGE_HI_KEY),
		RANGE_ROWS = convert(real,st.RANGE_ROWS),
		EQ_ROWS = convert(real,st.EQ_ROWS),
		DISTINCT_RANGE_ROWS = convert(real,st.DISTINCT_RANGE_ROWS),
		AVG_RANGE_ROWS = convert(real,st.AVG_RANGE_ROWS),
		[snapshot_time] = @snapshot_time,
		[snapshot_type_id] = @snapshot_type_id,
		[collection_time]
	from #stats st

	insert into [dbo].[sqlwatch_logger_index_histogram](
			[sqlwatch_database_id], [sqlwatch_table_id], [sqlwatch_index_id],
		RANGE_HI_KEY, RANGE_ROWS, EQ_ROWS, DISTINCT_RANGE_ROWS, AVG_RANGE_ROWS,
		[snapshot_time], [snapshot_type_id], [collection_time])
	select
		st.[sqlwatch_database_id],
		st.[sqlwatch_table_id],
		st.[sqlwatch_index_id],
		convert(nvarchar(max),st.RANGE_HI_KEY),
		RANGE_ROWS = convert(real,st.RANGE_ROWS),
		EQ_ROWS = convert(real,st.EQ_ROWS),
		DISTINCT_RANGE_ROWS = convert(real,st.DISTINCT_RANGE_ROWS),
		AVG_RANGE_ROWS = convert(real,st.AVG_RANGE_ROWS),
		[snapshot_time] = @snapshot_time,
		[snapshot_type_id] = @snapshot_type_id,
		[collection_time]
	from #stats_hierarchical st

	insert into [dbo].[sqlwatch_logger_index_histogram](
			[sqlwatch_database_id], [sqlwatch_table_id], [sqlwatch_index_id],
		RANGE_HI_KEY, RANGE_ROWS, EQ_ROWS, DISTINCT_RANGE_ROWS, AVG_RANGE_ROWS,
		[snapshot_time], [snapshot_type_id], [collection_time])
	select
		st.[sqlwatch_database_id],
		st.[sqlwatch_table_id],
		st.[sqlwatch_index_id],
		convert(nvarchar(max),st.RANGE_HI_KEY),
		RANGE_ROWS = convert(real,st.RANGE_ROWS),
		EQ_ROWS = convert(real,st.EQ_ROWS),
		DISTINCT_RANGE_ROWS = convert(real,st.DISTINCT_RANGE_ROWS),
		AVG_RANGE_ROWS = convert(real,st.AVG_RANGE_ROWS),
		[snapshot_time] = @snapshot_time,
		[snapshot_type_id] = @snapshot_type_id,
		[collection_time]
	from #stats_timestamp st


commit tran
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_logger_index_usage_stats]
	@databases varchar(max) = null,
	@ignore_global_exclusion bit = 0
AS

declare @index_usage_age smallint = [dbo].[ufn_sqlwatch_get_config_value] ( 14, null ),
		@index_batch_size smallint = [dbo].[ufn_sqlwatch_get_config_value] ( 15, null )

if @databases is null 
	begin
		set @databases = '-tempdb'
	end

-- if intelligent index stats collection is enabled,
-- reset database list as we're going to set it dynamically
if @index_usage_age >= 0
	begin

		select distinct database_name, table_name, index_name
		into ##sqlwatch_index_usage_stats_collector_1546356805384099A7534C851E48C6D1
		from (
			select distinct top (@index_batch_size) 
					  db.database_name
					, tb.table_name
					, id.index_name
					, us.snapshot_time
			from [dbo].[sqlwatch_logger_index_usage_stats] us
	
				inner join dbo.sqlwatch_meta_database db
				on db.sqlwatch_database_id = us.sqlwatch_database_id
				and db.sql_instance = us.sql_instance

				inner join dbo.sqlwatch_meta_table tb
				on tb.sqlwatch_database_id = us.sqlwatch_database_id
				and tb.sql_instance = us.sql_instance
				and tb.sqlwatch_table_id = us.sqlwatch_table_id

				inner join dbo.sqlwatch_meta_index id
				on id.sqlwatch_database_id = us.sqlwatch_database_id
				and id.sqlwatch_table_id = us.sqlwatch_table_id
				and id.sqlwatch_index_id = us.sqlwatch_index_id
				and id.sql_instance = us.sql_instance

			where snapshot_time < dateadd(minute,-@index_usage_age,getutcdate())
			and tb.table_type = 'BASE TABLE'
			order by snapshot_time asc
		) t

		create clustered index idx_tmp_sqlwatch_index_usage_stats_collector_1546356805384099A7534C851E48C6D1
		on ##sqlwatch_index_usage_stats_collector_1546356805384099A7534C851E48C6D1 (database_name, table_name, index_name)

		set @databases = null
		select @databases = @databases + ',' + database_name
		from (
			select distinct database_name
			from ##sqlwatch_index_usage_stats_collector_1546356805384099A7534C851E48C6D1
			) t

	end

set xact_abort on
set nocount on

declare @snapshot_time datetime2(0),
		@snapshot_type_id tinyint = 14,
		@database_name sysname,
		@sql varchar(max),
		@date_snapshot_previous datetime2(0),
		@object_id int,
		@index_name sysname,
		@index_id int,
		@object_name nvarchar(256)

select @date_snapshot_previous = max([snapshot_time])
	from [dbo].[sqlwatch_logger_snapshot_header] (nolock) --so we dont get blocked by central repository. this is safe at this point.
	where snapshot_type_id = @snapshot_type_id
	and sql_instance = @@SERVERNAME

	exec [dbo].[usp_sqlwatch_internal_insert_header] 
		@snapshot_time_new = @snapshot_time OUTPUT,
		@snapshot_type_id = @snapshot_type_id

/* step 2 , collect indexes from all databases */
		select @sql = 'insert into [dbo].[sqlwatch_logger_index_usage_stats] (
	sqlwatch_database_id, [sqlwatch_index_id], [used_pages_count],
	user_seeks, user_scans, user_lookups, user_updates, last_user_seek, last_user_scan, last_user_lookup, last_user_update,
	stats_date, snapshot_time, snapshot_type_id, index_disabled, partition_id, [sqlwatch_table_id],

	[used_pages_count_delta], [user_seeks_delta], [user_scans_delta], [user_updates_delta], [delta_seconds], [user_lookups_delta],
	[partition_count], [partition_count_delta]
	)
			select 
				mi.sqlwatch_database_id,
				mi.[sqlwatch_index_id],
				[used_page_count] = convert(real,ps.[used_page_count]),
				[user_seeks] = convert(real,ixus.[user_seeks]),
				[user_scans] = convert(real,ixus.[user_scans]),
				[user_lookups] = convert(real,ixus.[user_lookups]),
				[user_updates] = convert(real,ixus.[user_updates]),
				ixus.[last_user_seek],
				ixus.[last_user_scan],
				ixus.[last_user_lookup],
				ixus.[last_user_update],
				[stats_date]=STATS_DATE(ix.object_id, ix.index_id),
				[snapshot_time] = ''' + convert(varchar(23),@snapshot_time,121) + ''',
				[snapshot_type_id] = ' + convert(varchar(5),@snapshot_type_id) + ',
				[is_disabled]=ix.is_disabled,
				partition_id = -1,
				mi.sqlwatch_table_id

				, [used_pages_count_delta] = case when ps.[used_page_count] > usprev.[used_pages_count] then ps.[used_page_count] - usprev.[used_pages_count] else 0 end
				, [user_seeks_delta] = case when ixus.[user_seeks] > usprev.[user_seeks] then ixus.[user_seeks] - usprev.[user_seeks] else 0 end
				, [user_scans_delta] = case when ixus.[user_scans] > usprev.[user_scans] then ixus.[user_scans] - usprev.[user_scans] else 0 end
				, [user_updates_delta] = case when ixus.[user_updates] > usprev.[user_updates] then ixus.[user_updates] - usprev.[user_updates] else 0 end
				, [delta_seconds_delta] = datediff(second,''' + convert(varchar(23),@date_snapshot_previous,121) + ''',''' + convert(varchar(23),@snapshot_time,121) + ''')
				, [user_lookups_delta] = case when ixus.[user_lookups] > usprev.[user_lookups] then ixus.[user_lookups] - usprev.[user_lookups] else 0 end
				, [partition_count] = ps.partition_count
				, [partition_count_delta] = usprev.partition_count - ps.partition_count
			from sys.dm_db_index_usage_stats ixus

			inner join sys.databases dbs
				on dbs.database_id = ixus.database_id
				and dbs.name = ''?''

			inner join [?].sys.indexes ix 
				on ix.index_id = ixus.index_id
				and ix.object_id = ixus.object_id

			/*	to reduce size of the index stats table, we are going to aggreagte partitions into tables.
				from daily database management and DBA point of view, we care more about overall index stats rather than
				individual partitions.	*/
			inner join (select [object_id], [index_id], [used_page_count]=sum([used_page_count]), [partition_count]=count(*)
				from [?].sys.dm_db_partition_stats
				group by [object_id], [index_id]
				) ps 
				on  ps.[object_id] = ix.[object_id]
				and ps.[index_id] = ix.[index_id]

			inner join [?].sys.tables t 
				on t.[object_id] = ix.[object_id]

			inner join [?].sys.schemas s 
				on s.[schema_id] = t.[schema_id]

			inner join [dbo].[sqlwatch_meta_database] mdb
				on mdb.database_name = dbs.name collate database_default
				and mdb.database_create_date = dbs.create_date

			/* https://github.com/marcingminski/sqlwatch/issues/110 */
			inner join [dbo].[sqlwatch_meta_table] mt
				on mt.sql_instance = mdb.sql_instance
				and mt.sqlwatch_database_id = mdb.sqlwatch_database_id
				and mt.table_name = s.name + ''.'' + t.name collate database_default

			inner join [dbo].[sqlwatch_meta_index] mi
				on mi.sql_instance = @@SERVERNAME
				and mi.sqlwatch_database_id = mdb.sqlwatch_database_id
				and mi.sqlwatch_table_id = mt.sqlwatch_table_id
				and mi.index_id = ixus.index_id
				and mi.index_name = case when mi.index_type_desc = ''HEAP'' then t.[name] else ix.[name] end collate database_default


			' + case when @index_usage_age >= 0 then '
			inner join ##sqlwatch_index_usage_stats_collector_1546356805384099A7534C851E48C6D1 x
				on x.database_name = mdb.database_name
				and x.table_name = mt.table_name
				and x.index_name = mi.index_name
			
			' else '' end + '

			left join [dbo].[sqlwatch_logger_index_usage_stats] usprev
				on usprev.sql_instance = mi.sql_instance
				and usprev.sqlwatch_database_id = mi.sqlwatch_database_id
				and usprev.sqlwatch_table_id = mi.sqlwatch_table_id
				and usprev.sqlwatch_index_id = mi.sqlwatch_index_id
				and usprev.snapshot_type_id = ' + convert(varchar(5),@snapshot_type_id) + '
				and usprev.snapshot_time = ''' + convert(varchar(23),@date_snapshot_previous,121) + '''
				and usprev.partition_id = -1

			Print ''['' + convert(varchar(23),getdate(),121) + ''] Collecting index statistics for database: ?''
'

exec [dbo].[usp_sqlwatch_internal_foreachdb] 
	@command = @sql,
	@snapshot_type_id = @snapshot_type_id,
	@calling_proc_id = @@PROCID,
	@databases = @databases
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_logger_missing_index_stats]
AS

/*
-------------------------------------------------------------------------------------------------------------------
 Procedure:
	[dbo].[usp_sqlwatch_logger_missing_index_stats]

 Description:
	Captures Missing Indexes

 Parameters
	
 Author:
	Marcin Gminski

 Change Log:
	1.0		2018-09-22	- Colin Douglas:	Initial Version
	1.1		2019-11-17	- Marcin Gminski:	Exclude idle wait stats.
	1.2		2019-11-24	- Marcin Gminski:	Replace sys.databses with dbo.vw_sqlwatch_sys_databases
	1.3		2020-03-18	- Marcin Gminski,	move explicit transaction after header to fix https://github.com/marcingminski/sqlwatch/issues/155
-------------------------------------------------------------------------------------------------------------------
*/

set xact_abort on

	-- SET NOCOUNT ON added to prevent extra result sets from
	-- interfering with SELECT statements.
	SET NOCOUNT ON;
    
	--------------------------------------------------------------------------------------------------------------
    -- variables
	--------------------------------------------------------------------------------------------------------------
	declare @snapshot_time datetime,
			@snapshot_type_id tinyint = 3

	exec [dbo].[usp_sqlwatch_internal_insert_header] 
		@snapshot_time_new = @snapshot_time OUTPUT,
		@snapshot_type_id = @snapshot_type_id

	--only enterprise and developer will allow online index build/rebuild
	declare @allows_online_index bit
	select @allows_online_index = case 
			when 
					convert(varchar(4000),serverproperty('Edition')) like 'Enterprise%' 
				or	convert(varchar(4000),serverproperty('Edition')) like 'Developer%'
			then 1
			else 0
		end

	--------------------------------------------------------------------------------------------------------------
	-- get missing indexes
	--------------------------------------------------------------------------------------------------------------
begin tran

	insert into [dbo].[sqlwatch_logger_index_missing_stats] ([sqlwatch_database_id],
		[sqlwatch_table_id], [sqlwatch_missing_index_id],[snapshot_time], [last_user_seek], [unique_compiles],
		[user_seeks], [user_scans], [avg_total_user_cost], [avg_user_impact], [snapshot_type_id],[sql_instance])
	select 
		--[server_name] = @@servername ,
		[sqlwatch_database_id] = db.[sqlwatch_database_id], 
		[sqlwatch_table_id] = mt.[sqlwatch_table_id],
		[sqlwatch_missing_index_id] = mii.sqlwatch_missing_index_id,

		--[database_create_date] = db.[database_create_date],
		--[object_name] = parsename(mi.[statement],2) + '.' + parsename(mi.[statement],1), 
		[snapshot_time] = @snapshot_time,
		igs.[last_user_seek],
		igs.[unique_compiles], 
		igs.[user_seeks], 
		igs.[user_scans], 
		igs.[avg_total_user_cost], 
		igs.[avg_user_impact],
		[snapshot_type_id] = @snapshot_type_id,
		@@SERVERNAME
	from sys.dm_db_missing_index_groups ig 

		inner join sys.dm_db_missing_index_group_stats igs 
			on igs.group_handle = ig.index_group_handle 

		inner join sys.dm_db_missing_index_details mi 
			on ig.index_handle = mi.index_handle

		inner join dbo.vw_sqlwatch_sys_databases sdb
			on sdb.[name] = db_name(mi.[database_id])

		inner join [dbo].[sqlwatch_meta_database] db
			on db.[database_name] = db_name(mi.[database_id])
			and db.[database_create_date] = sdb.[create_date]
			and db.sql_instance = @@SERVERNAME

		inner join [dbo].[sqlwatch_meta_table] mt
			on mt.sql_instance = db.sql_instance
			and mt.sqlwatch_database_id = db.sqlwatch_database_id
			and mt.table_name = parsename(mi.[statement],2) + '.' + parsename(mi.[statement],1)

		inner join [dbo].[sqlwatch_meta_index_missing] mii
			on mii.sqlwatch_database_id = db.sqlwatch_database_id
			and mii.sqlwatch_table_id = mt.sqlwatch_table_id
			and mii.sql_instance = mt.sql_instance
			and mii.index_handle = ig.index_handle
			and mii.equality_columns = mi.equality_columns collate database_default
			and mii.statement = mi.statement collate database_default

		-- this is not required as in this case we not populating [dbo].[sqlwatch_meta_index_missing] at all to reduce noise
		--left join [dbo].[sqlwatch_config_logger_exclude_database] ed
		--	on db.[database_name] like ed.database_name_pattern
		--	and ed.snapshot_type_id = @snapshot_type_id

	where mi.equality_columns is not null
	and mi.statement is not null

commit tran
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_logger_performance] AS

set xact_abort on
set nocount on;

declare	@product_version nvarchar(128)
declare @product_version_major decimal(10,2)
declare @product_version_minor decimal(10,2)
declare	@sql_memory_mb int
declare @os_memory_mb int
declare @memory_available int
declare @percent_idle_time real
declare @percent_processor_time real
declare @date_snapshot_current datetime2(0)
declare @date_snapshot_previous datetime2(0)
declare @sql_instance varchar(32)

declare @snapshot_type_id tinyint = 1

declare @sql nvarchar(4000) 

		set @sql_instance = [dbo].[ufn_sqlwatch_get_servername]()
		--------------------------------------------------------------------------------------------------------------
		-- detect which version of sql we are running as some dmvs are different in different versions of sql
		--------------------------------------------------------------------------------------------------------------
		select 
			 @product_version_major = [dbo].[ufn_sqlwatch_get_product_version]('major')
			,@product_version_minor = [dbo].[ufn_sqlwatch_get_product_version]('minor')

		--------------------------------------------------------------------------------------------------------------
		-- set the basics
		--------------------------------------------------------------------------------------------------------------
		select @date_snapshot_previous = max([snapshot_time])
		from [dbo].[sqlwatch_logger_snapshot_header] (nolock) --so we dont get blocked by central repository. this is safe at this point.
		where snapshot_type_id = @snapshot_type_id
		and sql_instance = [dbo].[ufn_sqlwatch_get_servername]()
		
		exec [dbo].[usp_sqlwatch_internal_insert_header] 
			@snapshot_time_new = @date_snapshot_current OUTPUT,
			@snapshot_type_id = @snapshot_type_id

		
		--this procedure normally takes around 100ms to run but if we ever encounter any long locks, we should bail after 1 second so we don't hold up the queue.
		--it shuold never happen but if it does, this will make sure the proc isn't running forever.
		--notice we're setting the timeout after we have obtained the snapshot
		set lock_timeout 1000; 

		--------------------------------------------------------------------------------------------------------------
		-- 1. get cpu -- Ring Buffer updates once a minute so we're not going to get any better resolution than that
		-- When running more frequent that 1 minute, we need to get CPU from perf counters
		-- Tapping into ring buffer can be expensive as we have to parse the response so we're only going to do so
		-- hen we know the data has refreshed i.e. every 60 seconds
		--------------------------------------------------------------------------------------------------------------
		if datediff(second,
			isnull((select top 1 snapshot_time
			from [dbo].[sqlwatch_stage_ring_buffer]
			order by snapshot_time desc),'1970-01-01'),@date_snapshot_current) >= 60
			begin
				truncate table [dbo].[sqlwatch_stage_ring_buffer];

				insert into [dbo].[sqlwatch_stage_ring_buffer] (snapshot_time, percent_processor_time, percent_idle_time)
				select 
						--original PR https://github.com/marcingminski/sqlwatch/commit/b8a8a5bbaf134dcd6afb4d5b9fef13e052a5c164
						--by https://github.com/marcingminski/sqlwatch/commits?author=sporri
						@date_snapshot_current
					,	percent_processor_time=convert(real,ProcessUtilization)
					,	percent_idle_time=convert(real,SystemIdle)
				FROM ( 
						SELECT SystemIdle=record.value('(./Record/SchedulerMonitorEvent/SystemHealth/SystemIdle)[1]', 'int'), 
							ProcessUtilization=record.value('(./Record/SchedulerMonitorEvent/SystemHealth/ProcessUtilization)[1]', 'int')
						FROM ( 
							SELECT TOP 1 CONVERT(xml, record) AS [record] 
							FROM sys.dm_os_ring_buffers WITH (NOLOCK)
							WHERE ring_buffer_type = N'RING_BUFFER_SCHEDULER_MONITOR' collate database_default
							AND record LIKE N'%<SystemHealth>%' collate database_default
							ORDER BY [timestamp] DESC
							) AS x 
						) AS y
				OPTION (keep plan);
			end

		select top 1 
				@percent_processor_time = percent_processor_time
			,	@percent_idle_time = percent_idle_time
		from [dbo].[sqlwatch_stage_ring_buffer]
		order by snapshot_time desc
		option (keep plan)


	begin tran
		--------------------------------------------------------------------------------------------------------------
		-- 2. get perfomance counters
		-- this is where it gets interesting. there are several types of performance counters identified by the cntr_type
		-- depending on the type, we may have to calculate deltas or deviation from the base.

		-- cntr_type description from: 
		--	https://blogs.msdn.microsoft.com/psssql/2013/09/23/interpreting-the-counter-values-from-sys-dm_os_performance_counters/
		--  https://rtpsqlguy.wordpress.com/2009/08/11/sys-dm_os_performance_counters-explained/

		-- 65792 -> this counter value shows the last observed value directly. no calculation required.
		-- 537003264 and 1073939712 -> this is similar to the above 65792 but we must divide the results by the base
		--------------------------------------------------------------------------------------------------------------
		
		--2020-08-27 11:31 performance tweak to try and get the execution down from ~600ms to minimum. 
		-- as of 11:31 this is down to 23ms		
		--2021-04-06 further performance tweaks to reduce number of logical reads
		-- down from 1440 to 900 and exec time down t 16ms:
		create table #t (
			object_name nvarchar(128),
			counter_name nvarchar(128),
			instance_name nvarchar(128),
			cntr_value real,
			cntr_type nvarchar(128),
			base_counter_name nvarchar(128),
			base_cntr_value real,
			performance_counter_id smallint
		)

		--load OS counters via CLR if enabled:
		if dbo.ufn_sqlwatch_get_clr_collector_status() = 1
			begin
				-- this has to be dynamic otherwise if we have CLR disabled it will error even though it may never get here due to the global config
				insert into #t with (tablock)
				exec sp_executesql '
				select
					[object_name]=rtrim(pc2.[object_name])
					, counter_name=rtrim(pc2.[counter_name])
					, instance_name=rtrim(pc1.[instance_name])
					, x.cntr_value
					, x.cntr_type
					, base_counter_name = rtrim(sc.base_counter_name)
					, base_cntr_value = bc.cntr_value
					, pc1.performance_counter_id
				from [dbo].[sqlwatch_meta_performance_counter_instance] pc1 with (nolock)
				
				inner join dbo.sqlwatch_meta_performance_counter pc2 with (nolock)
					on pc1.performance_counter_id = pc2.performance_counter_id
					and pc1.sql_instance = pc2.sql_instance

				cross apply dbo.GetPerformnaceCounterData (pc2.object_name,pc2.counter_name,pc1.instance_name, null) x

				inner join dbo.[sqlwatch_config_performance_counters] sc with (nolock)
					on rtrim(pc2.[object_name]) like ''%'' + sc.[object_name] collate database_default
					and rtrim(pc2.counter_name) = sc.counter_name collate database_default
					and (
						rtrim(pc1.instance_name) = sc.instance_name collate database_default
						or	(
							sc.instance_name = ''<* !_Total>'' collate database_default
							and rtrim(pc1.instance_name) <> ''_Total'' collate database_default
							)
						)

				outer apply (
							select y.cntr_value
							from [dbo].[sqlwatch_meta_performance_counter_instance] pcb1 (nolock)

							inner join dbo.sqlwatch_meta_performance_counter pcb2 with (nolock)
								on pcb1.performance_counter_id = pcb2.performance_counter_id
								and pcb1.sql_instance = pcb2.sql_instance

							cross apply dbo.GetPerformnaceCounterData (pcb2.object_name,pcb2.counter_name,pc1.instance_name, null) y
							where y.cntr_type = 1073939712
								and pcb2.[object_name] = pc2.[object_name] collate database_default
								and pcb1.instance_name = pc1.instance_name collate database_default
								and pcb2.counter_name = sc.base_counter_name collate database_default
							) bc

				where sc.collect = 1
				and x.cntr_type <> 1073939712
				and pc2.is_sql_counter = 0

				option (maxdop 1, keep plan)
				'
			end

			--always load SQL counters from DMV:
			insert into #t with (tablock)
			select 
					pc.[object_name]
				, pc.[counter_name]
				, pc.[instance_name]
				, pc.cntr_value
				, pc.cntr_type
				, pc.base_counter_name
				, pc.base_cntr_value
				, mc.performance_counter_id
			from (
				select
						[object_name]=rtrim(pc1.[object_name])
				, counter_name=rtrim(pc1.[counter_name])
				, instance_name=rtrim(pc1.[instance_name])
				, pc1.cntr_value
				, pc1.cntr_type
				, base_counter_name = rtrim(sc.base_counter_name)
				, base_cntr_value = bc.cntr_value
				from sys.dm_os_performance_counters pc1 with (nolock)

				inner join dbo.[sqlwatch_config_performance_counters] sc with (nolock)
					on rtrim(pc1.[object_name]) like '%' + sc.[object_name] collate database_default
					and rtrim(pc1.counter_name) = sc.counter_name collate database_default
					and (
						rtrim(pc1.instance_name) = sc.instance_name collate database_default
						or	(
							sc.instance_name = '<* !_Total>' collate database_default
							and rtrim(pc1.instance_name) <> '_Total' collate database_default
							)
						)

				outer apply (
							select pcb.cntr_value
							from sys.dm_os_performance_counters pcb with (nolock)
							where pcb.cntr_type = 1073939712
								and pcb.[object_name] = pc1.[object_name] collate database_default
								and pcb.instance_name = pc1.instance_name collate database_default
								and pcb.counter_name = sc.base_counter_name collate database_default
							) bc

				where sc.collect = 1
				and pc1.cntr_type <> 1073939712

				union all
				/*  because we are only querying sql related performance counters (as only those are exposed through sql) we do not
					capture os performance counters such as cpu - hence we captured cpu from ringbuffer and now are going to 
					make them look like real counter (othwerwise i would have to make up a name) */
				select 
						[object_name] = 'Win32_PerfFormattedData_PerfOS_Processor'
					,[counter_name] = 'Processor Time %'
					,[instance_name] = 'sql'
					,[cntr_value] = @percent_processor_time
					,[cntr_type] = 65792
					,base_counter_name = null
					,base_cntr_value = null

				union all
				select 
						[object_name] = 'Win32_PerfFormattedData_PerfOS_Processor'
					,[counter_name] = 'Idle Time %'
					,[instance_name] = '_Total                                                                                                                          '
					,[cntr_value] = @percent_idle_time
					,[cntr_type] = 65792
					,base_counter_name = null
					,base_cntr_value = null

				union all
				select 
						[object_name] = 'Win32_PerfFormattedData_PerfOS_Processor'
					,[counter_name] = 'Processor Time %'
					,[instance_name] = 'system'
					,[cntr_value] = (100-@percent_idle_time-@percent_processor_time)
					,[cntr_type] = 65792
					,base_counter_name = null
					,base_cntr_value = null

				) pc

			inner join [dbo].[sqlwatch_meta_performance_counter] mc (nolock)
				on mc.[object_name] = pc.[object_name] collate database_default
				and mc.[counter_name] = pc.[counter_name] collate database_default
				and mc.[sql_instance] = @sql_instance

			where mc.is_sql_counter = 1

			option (maxdop 1, keep plan)

				;

			insert into dbo.[sqlwatch_logger_perf_os_performance_counters] ([performance_counter_id],[instance_name], [cntr_value], [base_cntr_value],
				[snapshot_time], [snapshot_type_id], [sql_instance], [cntr_value_calculated])
			select
					pc.[performance_counter_id]
				,instance_name = rtrim(pc.instance_name)
				,pc.cntr_value
				,base_cntr_value=pc.base_cntr_value
				,snapshot_time=@date_snapshot_current
				, @snapshot_type_id
				, @sql_instance
				,[cntr_value_calculated] = convert(real,(
					case 
						--https://docs.microsoft.com/en-us/dotnet/api/system.diagnostics.performancecountertype?view=netframework-4.8
						--https://docs.microsoft.com/en-us/dotnet/api/system.diagnostics.performancedata.countertype?view=netframework-4.8
						when pc.object_name = 'Batch Resp Statistics' then case when pc.cntr_value > prev.cntr_value then cast((pc.cntr_value - prev.cntr_value) as real) else 0 end -- delta absolute
					
						/*	65792
							An instantaneous counter that shows the most recently observed value. Used, for example, to maintain a simple count of a very large number of items or operations. 
							It is the same as NumberOfItems32 except that it uses larger fields to accommodate larger values.	*/
						when pc.cntr_type = 65792 then isnull(pc.cntr_value,0) 	
					
						/*	272696576
							A difference counter that shows the average number of operations completed during each second of the sample interval. Counters of this type measure time in ticks of the system clock. 
							This counter type is the same as the RateOfCountsPerSecond32 type, but it uses larger fields to accommodate larger values to track a high-volume number of items or operations per second, 
							such as a byte-transmission rate. Counters of this type include System\ File Read Bytes/sec.	*/
						when pc.cntr_type = 272696576 then case when (pc.cntr_value > prev.cntr_value) then (pc.cntr_value - prev.cntr_value) / cast(datediff(second,prev.snapshot_time,@date_snapshot_current) as real) else 0 end -- delta rate
					
						/*	537003264	
							This counter type shows the ratio of a subset to its set as a percentage. For example, it compares the number of bytes in use on a disk to the total number of bytes on the disk. 
							Counters of this type display the current percentage only, not an average over time. It is the same as the RawFraction32 counter type, except that it uses larger fields to accommodate larger values.	*/
						when pc.cntr_type = 537003264 then isnull(cast(100.0 as real) * pc.cntr_value / nullif(pc.base_cntr_value, 0),0) -- ratio

						/*	1073874176		
							An average counter that shows how many items are processed, on average, during an operation. Counters of this type display a ratio of the items processed to the number of operations completed. 
							The ratio is calculated by comparing the number of items processed during the last interval to the number of operations completed during the last interval. 
							Counters of this type include PhysicalDisk\ Avg. Disk Bytes/Transfer.	*/
						when pc.cntr_type = 1073874176 then isnull(case when pc.cntr_value > prev.cntr_value then isnull((pc.cntr_value - prev.cntr_value) / nullif(pc.base_cntr_value - prev.base_cntr_value, 0) / cast(datediff(second,prev.snapshot_time,@date_snapshot_current) as real), 0) else 0 end,0) -- delta ratio

						--any other not in the will need to be pre-calculated, for example from CLR, such as CPU %.
						else pc.cntr_value
					end))
			from #t pc

			left join [dbo].[sqlwatch_logger_perf_os_performance_counters] prev (nolock) --previous
				on prev.snapshot_time = @date_snapshot_previous
				and prev.performance_counter_id = pc.performance_counter_id
				and prev.instance_name = pc.instance_name collate database_default
				and prev.sql_instance = @sql_instance
				and prev.snapshot_type_id = 1
		
			option (maxdop 1, keep plan)


		--------------------------------------------------------------------------------------------------------------
		-- get schedulers summary
		--------------------------------------------------------------------------------------------------------------
		insert into dbo.[sqlwatch_logger_perf_os_schedulers]
			select 
				  snapshot_time = @date_snapshot_current
				, snapshot_type_id = @snapshot_type_id
				, scheduler_count = sum(case when is_online = 1 then 1 else 0 end)
				, [idle_scheduler_count] = sum(convert(int,is_idle))
				, current_tasks_count = sum(current_tasks_count)
				, runnable_tasks_count = sum(runnable_tasks_count)

				, preemptive_switches_count = sum(convert(bigint,preemptive_switches_count))
				, context_switches_count = sum(convert(bigint,context_switches_count))
				, idle_switches_count = sum(convert(bigint,context_switches_count))

				, current_workers_count = sum(current_workers_count)
				, active_workers_count = sum(active_workers_count)
				, work_queue_count = sum(work_queue_count)
				, pending_disk_io_count = sum(pending_disk_io_count)
				, load_factor = sum(load_factor)

				, yield_count = sum(convert(bigint,yield_count))

				, failed_to_create_worker = sum(convert(int,failed_to_create_worker))

				/* 2016 onwards only */
				, total_cpu_usage_ms = null --sum(convert(bigint,total_cpu_usage_ms))
				, total_scheduler_delay_ms = null --sum(convert(bigint,total_scheduler_delay_ms))

				, @sql_instance
			from sys.dm_os_schedulers
			where scheduler_id < 255
			and status = 'VISIBLE ONLINE' collate database_default
			option (keep plan)

		--------------------------------------------------------------------------------------------------------------
		-- get process memory
		--------------------------------------------------------------------------------------------------------------
		insert into dbo.[sqlwatch_logger_perf_os_process_memory]
		select snapshot_time=@date_snapshot_current, * , 1, @sql_instance
		from sys.dm_os_process_memory
		option (keep plan)

		--------------------------------------------------------------------------------------------------------------
		-- get sql memory. dynamic again based on sql version
		-- based on [msdb].[dbo].[syscollector_collection_items]
		--------------------------------------------------------------------------------------------------------------
		declare @dm_os_memory_clerks table (
			[type] varchar(60),
			memory_node_id smallint,
			single_pages_kb bigint,
			multi_pages_kb bigint,
			virtual_memory_reserved_kb bigint,
			virtual_memory_committed_kb bigint,
			awe_allocated_kb bigint,
			shared_memory_reserved_kb bigint,
			shared_memory_committed_kb bigint
		)
		if @product_version_major < 11
			begin
				insert into @dm_os_memory_clerks
				exec sp_executesql N'
				select 
					type,
					memory_node_id as memory_node_id,
					sum(single_pages_kb) as single_pages_kb,
					0 as multi_pages_kb,
					sum(virtual_memory_reserved_kb) as virtual_memory_reserved_kb,
					sum(virtual_memory_committed_kb) as virtual_memory_committed_kb,
					sum(awe_allocated_kb) as awe_allocated_kb,
					sum(shared_memory_reserved_kb) as shared_memory_reserved_kb,
					sum(shared_memory_committed_kb) as shared_memory_committed_kb
				from sys.dm_os_memory_clerks mc
				group by type, memory_node_id
				option (keep plan)
				'
			end
		else
			begin
				insert into @dm_os_memory_clerks
				exec sp_executesql N'
				select 
					type,
					memory_node_id as memory_node_id,
					sum(pages_kb) as single_pages_kb,
					0 as multi_pages_kb,
					sum(virtual_memory_reserved_kb) as virtual_memory_reserved_kb,
					sum(virtual_memory_committed_kb) as virtual_memory_committed_kb,
					sum(awe_allocated_kb) as awe_allocated_kb,
					sum(shared_memory_reserved_kb) as shared_memory_reserved_kb,
					sum(shared_memory_committed_kb) as shared_memory_committed_kb
				from sys.dm_os_memory_clerks
				group by type, memory_node_id
				option (keep plan)
			'
			end

		declare @memory_clerks table (
			[type] varchar(60),
			memory_node_id smallint,
			single_pages_kb bigint,
			multi_pages_kb bigint,
			virtual_memory_reserved_kb bigint,
			virtual_memory_committed_kb bigint,
			awe_allocated_kb bigint,
			shared_memory_reserved_kb bigint,
			shared_memory_committed_kb bigint,
			snapshot_time datetime,
			total_kb bigint
		)
		insert into @memory_clerks
		select 
			mc.[type], mc.memory_node_id, mc.single_pages_kb, mc.multi_pages_kb, mc.virtual_memory_reserved_kb, 
			mc.virtual_memory_committed_kb, mc.awe_allocated_kb, mc.shared_memory_reserved_kb, mc.shared_memory_committed_kb, 
			snapshot_time = @date_snapshot_current, 
			cast (mc.single_pages_kb as bigint) 
				+ mc.multi_pages_kb 
				+ (case when type <> 'MEMORYCLERK_SQLBUFFERPOOL' collate database_default then mc.virtual_memory_committed_kb else 0 end) 
				+ mc.shared_memory_committed_kb as total_kb
		from @dm_os_memory_clerks as mc
		option (keep plan)

		insert into dbo.[sqlwatch_logger_perf_os_memory_clerks]
		select t.snapshot_time, t.total_kb, t.allocated_kb,  mm.sqlwatch_mem_clerk_id
			, t.[snapshot_type_id], t.[sql_instance]
		from (
			select 
				snapshot_time =@date_snapshot_current
				, total_kb=sum(mc.total_kb)
				, allocated_kb=sum(mc.single_pages_kb + mc.multi_pages_kb)
				 -- There are many memory clerks. We will log any that make up 5% of sql memory or more; less significant clerks will be lumped into an "other" bucket
				 -- this approach will save storage whilst retaining enough detail for troubleshooting. 
				 -- if you want to see more or less clerks, you can adjust it here, or even remove entirely to log all clerks
				 -- In my test enviroment, the summary of all clerks, i.e. a clerk across all nodes and addresses will give approx 87 rows, 
				 -- the below approach gives ~6 rows on average but your mileage will vary.
				, [type] = case when mc.total_kb / convert(decimal, ta.total_kb_all_clerks) > 0.05 then mc.[type] else N'OTHER' end
				, [snapshot_type_id] = @snapshot_type_id
				, [sql_instance] = [dbo].[ufn_sqlwatch_get_servername]()
			from @memory_clerks as mc
			outer apply 
			(	select 
					sum (mc_ta.total_kb) as total_kb_all_clerks
				from @memory_clerks as mc_ta
			) as ta
			group by mc.snapshot_time, case when mc.total_kb / convert(decimal, ta.total_kb_all_clerks) > 0.05 then mc.[type] else N'OTHER' end
		) t
		inner join [dbo].[sqlwatch_meta_memory_clerk] mm
			on mm.clerk_name = t.[type] collate database_default
			and mm.sql_instance = @sql_instance
		option (keep plan)					

		--------------------------------------------------------------------------------------------------------------
		-- file stats snapshot
		--------------------------------------------------------------------------------------------------------------
		select *
		into #fs
		from [dbo].[sqlwatch_logger_perf_file_stats] (nolock) prevfs
		where prevfs.sql_instance = @sql_instance
			and prevfs.snapshot_type_id = @snapshot_type_id
			and prevfs.snapshot_time = @date_snapshot_previous
		option (keep plan)

		create unique clustered index idx_tmp_fs 
			on #fs (sql_instance,sqlwatch_database_id,sqlwatch_master_file_id)

		--reduce compile time of the big query below
		select d.database_id , sd.sqlwatch_database_id, sd.sql_instance
		into #d
		from dbo.vw_sqlwatch_sys_databases d

		inner join [dbo].[sqlwatch_meta_database] sd 
			on sd.[database_name] = d.[name] collate database_default
			and sd.[database_create_date] = case when d.name = 'tempdb' then '1970-01-01 00:00:00.000' else d.[create_date] end
			and sd.sql_instance = @sql_instance

		left join [dbo].[sqlwatch_config_exclude_database] ed
			on d.[name] like ed.database_name_pattern collate database_default
			and ed.snapshot_type_id = @snapshot_type_id
		where ed.snapshot_type_id is null
		option (keep plan)

		create unique clustered index idx_tmp_d
			on #d (database_id)

		insert into dbo.[sqlwatch_logger_perf_file_stats] (
			[sqlwatch_database_id]
           ,[sqlwatch_master_file_id]
		   ,[num_of_reads],[num_of_bytes_read],[io_stall_read_ms],[num_of_writes],[num_of_bytes_written],[io_stall_write_ms],[size_on_disk_bytes]
		   ,[snapshot_time]
		   ,[snapshot_type_id]
		   ,[sql_instance]
		   
		   , [num_of_reads_delta]
		   , [num_of_bytes_read_delta]
		   , [io_stall_read_ms_delta]
		   , [num_of_writes_delta]
		   , [num_of_bytes_written_delta]
		   , [io_stall_write_ms_delta]
		   , [size_on_disk_bytes_delta]
		   , [delta_seconds]
		   )
		select 
			 d.sqlwatch_database_id
			,mf.sqlwatch_master_file_id
			,num_of_reads = convert(real,fs.num_of_reads)
			, num_of_bytes_read = convert(real,fs.num_of_bytes_read)
			, io_stall_read_ms = convert(real,fs.io_stall_read_ms)
			, num_of_writes = convert(real,fs.num_of_writes)
			, num_of_bytes_written = convert(real,fs.num_of_bytes_written)
			, io_stall_write_ms = convert(real,fs.io_stall_write_ms)
			, size_on_disk_bytes = convert(real,fs.size_on_disk_bytes)
			, snapshot_time=@date_snapshot_current
			, @snapshot_type_id
			, @@SERVERNAME

			, [num_of_reads_delta] = convert(real,case when fs.num_of_reads > prevfs.num_of_reads then fs.num_of_reads - prevfs.num_of_reads else 0 end)
			, [num_of_bytes_read_delta] = convert(real,case when fs.num_of_bytes_read > prevfs.num_of_bytes_read then fs.num_of_bytes_read - prevfs.num_of_bytes_read else 0 end)
			, [io_stall_read_ms_delta] = convert(real,case when fs.io_stall_read_ms > prevfs.io_stall_read_ms then fs.io_stall_read_ms - prevfs.io_stall_read_ms else 0 end)
			, [num_of_writes_delta]= convert(real,case when fs.num_of_writes > prevfs.num_of_writes then fs.num_of_writes - prevfs.num_of_writes else 0 end)
			, [num_of_bytes_written_delta] = convert(real,case when fs.num_of_bytes_written > prevfs.num_of_bytes_written then fs.num_of_bytes_written - prevfs.num_of_bytes_written else 0 end)
			, [io_stall_write_ms_delta] = convert(real,case when fs.io_stall_write_ms > prevfs.io_stall_write_ms then fs.io_stall_write_ms - prevfs.io_stall_write_ms else 0 end)
			, [size_on_disk_bytes_delta] = convert(real,case when fs.size_on_disk_bytes > prevfs.size_on_disk_bytes then fs.size_on_disk_bytes - prevfs.size_on_disk_bytes else 0 end)
			, [delta_seconds] = datediff(second,@date_snapshot_previous,@date_snapshot_current)

		from sys.dm_io_virtual_file_stats (default, default) as fs
		inner join sys.master_files as f  with (nolock)
			on fs.database_id = f.database_id 
			and fs.[file_id] = f.[file_id]
		
		/* 2019-05-05 join on databases to get database name and create data as part of the 
		   -- doesnt this need a join on dbo.vw_sqlwatch_sys_databases instead ?
		   2019-11-24 change sys.databses to vw_sqlwatch_sys_databases */
		inner join #d d 
			on d.database_id = f.database_id

		--inner join [dbo].[sqlwatch_meta_database] sd 
		--	on sd.[database_name] = convert(nvarchar(128),d.[name]) collate database_default
		--	and sd.[database_create_date] = d.[create_date]
		--	and sd.sql_instance = [dbo].[ufn_sqlwatch_get_servername]()

		inner join [dbo].[sqlwatch_meta_master_file] mf
			on mf.sql_instance = d.sql_instance
			and mf.sqlwatch_database_id = d.sqlwatch_database_id
			and mf.file_name = convert(nvarchar(128),f.name) collate database_default
			and mf.[file_physical_name] = convert(nvarchar(260),f.physical_name) collate database_default

		/* 2019-10-21 pushing delta calculation to collector to improve reporting performance */
		left join #fs prevfs
			on prevfs.sql_instance = mf.sql_instance
			and prevfs.sqlwatch_database_id = mf.sqlwatch_database_id
			and prevfs.sqlwatch_master_file_id = mf.sqlwatch_master_file_id

		option (keepfixed plan)


		--------------------------------------------------------------------------------------------------------------
		/*	wait stats snapshot
			 READ ME!!

			 In previous versions we were capturing all waits that had a wait (waiting_tasks_count > 0)
			 ideally, this needs similar approach to the memory clerks where we only capture waits that actually matter.
			 or those that make up 95% of waits and ignore the noise. There is still a lot of noise despite the filter:
			 ws.waiting_tasks_count + ws.wait_time_ms + ws.max_wait_time_ms + ws.signal_wait_time_ms > 0
			 some waits are significant but have no delta over longer period of time.

			 However, the difficulty is, if we only record those that have had positive delta we may lose some waits
			 imagine the following scenario:

			 SNAPSHOT1, WAIT1,  [waiting_tasks_count] = 1,	[waiting_tasks_count_delta] = 0
			 SNAPSHOT2, WAIT1,	[waiting_tasks_count] = 2,  [waiting_tasks_count_delta] = 2-1 = 1

			 if we only record those with positive delta, we would have never captured the first occurence and thus
			 the second occurence would have had zero delta and we would not record it either. 

			 Also, because we are currently only capturing those with positive task count, there could be the following:

			 SNAPSHOT1, WAIT1,  [waiting_tasks_count] = 0,	[waiting_tasks_count_delta] = 0
			 SNAPSHOT2, WAIT1,	[waiting_tasks_count] = 100,  [waiting_tasks_count_delta] = 100 - 0 = 100

			 but, what we are going to show is this:

			 --> NOT CAPTURED:	SNAPSHOT1, WAIT1,	[waiting_tasks_count] = 0,		[waiting_tasks_count_delta] = 0
								SNAPSHOT2, WAIT1,	[waiting_tasks_count] = 100,	[waiting_tasks_count_delta] = 0

		     one way to solve is it to delete old snapshots that either have zero delta or zer0 waiting task count and 
			 only keep all values in the most recent snapshot
		*/
		--------------------------------------------------------------------------------------------------------------

		-- moving join on meta to stage brings the execution down from 42ms to 14ms
		insert into [dbo].[sqlwatch_stage_perf_os_wait_stats] with (tablock)
		select ws.* , @date_snapshot_current, ms.wait_type_id
		from sys.dm_os_wait_stats ws (nolock)

		inner join [dbo].[sqlwatch_meta_wait_stats] ms
			on ms.[wait_type] = ws.[wait_type] collate database_default
			and ms.[sql_instance] = @sql_instance

		-- exclude idle waits and noise
		where ws.wait_type not like 'SLEEP_%'
		and ms.[is_excluded] = 0
		option (keep plan)

		insert into [dbo].[sqlwatch_logger_perf_os_wait_stats]
			select 
				  [wait_type_id] = ws.[wait_type_id]
				, [waiting_tasks_count] = convert(real,ws.[waiting_tasks_count])
				, [wait_time_ms] = convert(real,ws.[wait_time_ms])
				, [max_wait_time_ms] = convert(real,ws.[max_wait_time_ms])
				, [signal_wait_time_ms] = convert(real,ws.[signal_wait_time_ms])
				
				, [snapshot_time]=@date_snapshot_current
				, @snapshot_type_id, @sql_instance

			, [waiting_tasks_count_delta] = convert(real,case when ws.[waiting_tasks_count] > wsprev.[waiting_tasks_count] then ws.[waiting_tasks_count] - wsprev.[waiting_tasks_count] else 0 end)
			, [wait_time_ms_delta] = convert(real,case when ws.[wait_time_ms] > wsprev.[wait_time_ms] then ws.[wait_time_ms] - wsprev.[wait_time_ms] else 0 end)
			, [max_wait_time_ms_delta] = convert(real,case when ws.[max_wait_time_ms] > wsprev.[max_wait_time_ms] then ws.[max_wait_time_ms] - wsprev.[max_wait_time_ms] else 0 end)
			, [signal_wait_time_ms_delta] = convert(real,case when ws.[signal_wait_time_ms] > wsprev.[signal_wait_time_ms] then ws.[signal_wait_time_ms] - wsprev.[signal_wait_time_ms] else 0 end)
			, [delta_seconds] = datediff(second,@date_snapshot_previous,@date_snapshot_current)
			from [dbo].[sqlwatch_stage_perf_os_wait_stats] ws

			left join [dbo].[sqlwatch_stage_perf_os_wait_stats] wsprev
				on wsprev.wait_type = ws.wait_type
				and wsprev.snapshot_time = @date_snapshot_previous

			where ws.snapshot_time = @date_snapshot_current
			and ws.[waiting_tasks_count] - wsprev.[waiting_tasks_count]  > 0
		option (keepfixed plan)

		delete from [dbo].[sqlwatch_stage_perf_os_wait_stats] with (tablock)
		where snapshot_time < @date_snapshot_current
		option (keep plan)

commit tran
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_logger_procedure_stats]
as

begin

	set nocount on;
	set xact_abort on;

	declare @snapshot_type_id smallint = 27,
			@snapshot_time datetime2(0),
			@date_snapshot_previous datetime2(0),
			@sql_instance varchar(32) = [dbo].[ufn_sqlwatch_get_servername]()

	select @date_snapshot_previous = max([snapshot_time])
	from [dbo].[sqlwatch_logger_snapshot_header] (nolock) --so we dont get blocked by central repository. this is safe at this point.
	where snapshot_type_id = @snapshot_type_id
	and sql_instance = @sql_instance
	option (keep plan);

	select 
		  sql_instance
		, sqlwatch_database_id
		, [sqlwatch_procedure_id]
		, total_worker_time
		, total_physical_reads
		, total_logical_writes
		, total_logical_reads
		, total_elapsed_time
		, cached_time
		, last_execution_time
		, execution_count
	into #t
	from [dbo].[sqlwatch_logger_perf_procedure_stats]
	where sql_instance = @sql_instance
	and snapshot_type_id = @snapshot_type_id
	and snapshot_time = @date_snapshot_previous
	option (keep plan);
	
	create unique clustered index icx_tmp_t1 on #t (sql_instance,sqlwatch_database_id,[sqlwatch_procedure_id], cached_time)

	exec [dbo].[usp_sqlwatch_internal_insert_header] 
		@snapshot_time_new = @snapshot_time OUTPUT,
		@snapshot_type_id = @snapshot_type_id	

	insert into [dbo].[sqlwatch_logger_perf_procedure_stats] (
			[cached_time] ,
			[last_execution_time] ,

			[execution_count] ,
			[total_worker_time] ,
			[last_worker_time] ,
			[min_worker_time] ,
			[max_worker_time] ,
			[total_physical_reads] ,
			[last_physical_reads] ,
			[min_physical_reads] ,
			[max_physical_reads] ,
			[total_logical_writes] ,
			[last_logical_writes] ,
			[min_logical_writes] ,
			[max_logical_writes] ,
			[total_logical_reads],
			[last_logical_reads] ,
			[min_logical_reads] ,
			[max_logical_reads] ,
			[total_elapsed_time],
			[last_elapsed_time] ,
			[min_elapsed_time] ,
			[max_elapsed_time]

			,delta_worker_time
			,delta_physical_reads
			,delta_logical_writes
			,delta_logical_reads
			,delta_elapsed_time
			,delta_execution_count

			,[sql_instance]
			,[sqlwatch_database_id]
			,[sqlwatch_procedure_id]
			,[snapshot_time] 
			,[snapshot_type_id] 

	)

	--get procedure stats:
	select 
		  ps.cached_time	
		, PS.last_execution_time

		, execution_count=convert(real,ps.execution_count)
		, total_worker_time=convert(real,ps.total_worker_time)
		, last_worker_time=convert(real,last_worker_time)
		, min_worker_time=convert(real,min_worker_time)
		, max_worker_time=convert(real,max_worker_time)	
		, total_physical_reads=convert(real,ps.total_physical_reads)	
		, last_physical_reads=convert(real,last_physical_reads)	
		, min_physical_reads=convert(real,min_physical_reads)	
		, max_physical_reads=convert(real,max_physical_reads)	
		, total_logical_writes=convert(real,ps.total_logical_writes)	
		, last_logical_writes=convert(real,last_logical_writes)	
		, min_logical_writes=convert(real,min_logical_writes)	
		, max_logical_writes=convert(real,max_logical_writes)	
		, total_logical_reads=convert(real,ps.total_logical_reads)	
		, last_logical_reads=convert(real,last_logical_reads)	
		, min_logical_reads=convert(real,min_logical_reads)	
		, max_logical_reads=convert(real,max_logical_reads)	
		, total_elapsed_time=convert(real,ps.total_elapsed_time)	
		, last_elapsed_time=convert(real,last_elapsed_time)	
		, min_elapsed_time=convert(real,min_elapsed_time)	
		, max_elapsed_time=convert(real,max_elapsed_time)

		, delta_worker_time=convert(real,case when ps.total_worker_time > isnull(prev.total_worker_time,0) then ps.total_worker_time - isnull(prev.total_worker_time,0) else 0 end)
		, delta_physical_reads=convert(real,case when ps.total_physical_reads > isnull(prev.total_physical_reads,0) then ps.total_physical_reads - isnull(prev.total_physical_reads,0) else 0 end)
		, delta_logical_writes=convert(real,case when ps.total_logical_writes > isnull(prev.total_logical_writes,0) then ps.total_logical_writes - isnull(prev.total_logical_writes,0) else 0 end)
		, delta_logical_reads=convert(real,case when ps.total_logical_reads > isnull(prev.total_logical_reads,0) then ps.total_logical_reads - isnull(prev.total_logical_reads,0) else 0 end)
		, delta_elapsed_time=convert(real,case when ps.total_elapsed_time > isnull(prev.total_elapsed_time,0) then ps.total_elapsed_time - isnull(prev.total_elapsed_time,0) else 0 end)
		, delta_execution_count=convert(real,case when ps.execution_count> isnull(prev.execution_count,0) then ps.execution_count - isnull(prev.execution_count,0) else 0 end)

		, @sql_instance
		, sd.sqlwatch_database_id
		, p.sqlwatch_procedure_id
		, @snapshot_time
		, @snapshot_type_id

	from sys.dm_exec_procedure_stats ps (nolock)

	inner join dbo.vw_sqlwatch_sys_databases d
		on d.database_id = ps.database_id
	
	inner join dbo.sqlwatch_meta_database sd
		on sd.database_name = d.name collate database_default
		and sd.database_create_date = d.create_date
		and sd.sql_instance = d.sql_instance

	inner join dbo.sqlwatch_meta_procedure p
		on p.procedure_name = object_schema_name(ps.object_id, ps.database_id) + '.' + object_name(ps.object_id, ps.database_id)
		and p.sql_instance = @sql_instance
		and p.sqlwatch_database_id = sd.sqlwatch_database_id

	left join [dbo].[sqlwatch_config_exclude_procedure] ex
		on sd.database_name like ex.database_name_pattern
		and p.procedure_name like ex.procedure_name_pattern
		and ex.snapshot_type_id = @snapshot_type_id

	left join #t prev
		on prev.sql_instance = sd.sql_instance
		and prev.sqlwatch_database_id = sd.sqlwatch_database_id
		and prev.[sqlwatch_procedure_id] = p.[sqlwatch_procedure_id]
		and prev.cached_time = ps.cached_time

	where ps.type = 'P'
	and ex.snapshot_type_id is null
	and (
		ps.last_execution_time > prev.last_execution_time
		or prev.last_execution_time is null
	)

	option (keep plan);


end
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_logger_query_stats]
as

begin
	set nocount on;
	set xact_abort on;

	declare @snapshot_type_id smallint = 28,
			@snapshot_time datetime2(0),
			@date_snapshot_previous datetime2(0),
			@sql_instance varchar(32) = [dbo].[ufn_sqlwatch_get_servername](),
			@sql_version smallint = [dbo].[ufn_sqlwatch_get_sql_version](),
			@sql nvarchar(max) = '',
			@sql_params nvarchar(max) = '';

	select @date_snapshot_previous = max([snapshot_time])
	from [dbo].[sqlwatch_logger_snapshot_header] (nolock) --so we dont get blocked by central repository. this is safe at this point.
	where snapshot_type_id = @snapshot_type_id
	and sql_instance = @sql_instance ;

	select 
		  [sql_instance]
		, plan_handle
		, statement_start_offset
		, statement_end_offset
		, total_worker_time
		, total_physical_reads
		, total_logical_writes
		, total_logical_reads
		, total_elapsed_time
		, creation_time
		, last_execution_time
		, snapshot_time
	into #t
	from [dbo].[sqlwatch_logger_perf_query_stats]
	where sql_instance = @sql_instance 
	and snapshot_type_id = @snapshot_type_id
	and snapshot_time = @date_snapshot_previous;

	create unique clustered index icx_tmp_query_stats_prev on #t ([sql_instance],plan_handle,statement_start_offset, statement_end_offset, [creation_time]);

	select qs.*
	into #s
	from sys.dm_exec_query_stats qs	

	cross apply sys.dm_exec_text_query_plan([plan_handle], [statement_start_offset], [statement_end_offset]) qp

	where last_execution_time > isnull((
		select max(last_execution_time) from #t
		),'1970-01-01')

	--not stored procedures as we're collecting stored procedures elsewhere.
	and qp.objectid is null ;

	--normalise query text and plans
	declare @plan_handle_table dbo.utype_plan_handle;

	insert into @plan_handle_table (plan_handle, statement_start_offset, statement_end_offset )
	select distinct plan_handle,  statement_start_offset, statement_end_offset
	from #s
	;

	declare @sqlwatch_plan_id dbo.utype_plan_id
	insert into @sqlwatch_plan_id 
	exec [dbo].[usp_sqlwatch_internal_get_query_plans]
		@plan_handle = @plan_handle_table, 
		@sql_instance = @sql_instance
	;

	exec [dbo].[usp_sqlwatch_internal_insert_header] 
		@snapshot_time_new = @snapshot_time OUTPUT,
		@snapshot_type_id = @snapshot_type_id;

	set @sql_params = '@snapshot_type_id smallint, @snapshot_time datetime2(0),@sql_instance varchar(32)';

	set @sql = '
	insert into [dbo].[sqlwatch_logger_perf_query_stats] (
		[sql_instance] ,
		[snapshot_time] ,
		[snapshot_type_id]

		,plan_handle
		,statement_start_offset
		,statement_end_offset
		,creation_time	
		,last_execution_time	

		,execution_count	
		,total_worker_time	
		,last_worker_time	
		,min_worker_time	
		,max_worker_time	
		,total_physical_reads	
		,last_physical_reads	
		,min_physical_reads	
		,max_physical_reads	
		,total_logical_writes	
		,last_logical_writes	
		,min_logical_writes	
		,max_logical_writes	
		,total_logical_reads	
		,last_logical_reads	
		,min_logical_reads	
		,max_logical_reads	
		,total_clr_time	
		,last_clr_time	
		,min_clr_time	
		,max_clr_time	
		,total_elapsed_time	
		,last_elapsed_time	
		,min_elapsed_time	
		,max_elapsed_time	
		,total_rows	
		,last_rows	
		,min_rows	
		,max_rows	
		,total_dop	
		,last_dop	
		,min_dop	
		,max_dop	
		,total_grant_kb	
		,last_grant_kb	
		,min_grant_kb	
		,max_grant_kb	
		,total_used_grant_kb	
		,last_used_grant_kb	
		,min_used_grant_kb	
		,max_used_grant_kb	
		,total_ideal_grant_kb	
		,last_ideal_grant_kb	
		,min_ideal_grant_kb	
		,max_ideal_grant_kb	
		,total_reserved_threads	
		,last_reserved_threads	
		,min_reserved_threads	
		,max_reserved_threads	
		,total_used_threads	
		,last_used_threads	
		,min_used_threads	
		,max_used_threads

		,delta_worker_time 
		,delta_physical_reads
		,delta_logical_writes
		,delta_logical_reads 
		,delta_elapsed_time 
		,delta_time_s
	)
	select 
		[sql_instance] = @sql_instance ,
		[snapshot_time] = @snapshot_time,
		[snapshot_type_id] = @snapshot_type_id

		,qs.plan_handle
		,qs.[statement_start_offset]
		,qs.[statement_end_offset]
		,qs.creation_time	
		,qs.last_execution_time	

		,qs.execution_count	
		,qs.total_worker_time	
		,qs.last_worker_time	
		,qs.min_worker_time	
		,qs.max_worker_time	
		,qs.total_physical_reads	
		,qs.last_physical_reads	
		,qs.min_physical_reads	
		,qs.max_physical_reads	
		,qs.total_logical_writes	
		,qs.last_logical_writes	
		,qs.min_logical_writes	
		,qs.max_logical_writes	
		,qs.total_logical_reads	
		,qs.last_logical_reads	
		,qs.min_logical_reads	
		,qs.max_logical_reads	
		,qs.total_clr_time	
		,qs.last_clr_time	
		,qs.min_clr_time	
		,qs.max_clr_time	
		,qs.total_elapsed_time	
		,qs.last_elapsed_time	
		,qs.min_elapsed_time	
		,qs.max_elapsed_time	
		,qs.total_rows	
		,qs.last_rows	
		,qs.min_rows	
		,qs.max_rows	
		' + case when @sql_version >= 2016 then '
			,qs.total_dop	
			,qs.last_dop	
			,qs.min_dop	
			,qs.max_dop	
			,qs.total_grant_kb	
			,qs.last_grant_kb	
			,qs.min_grant_kb	
			,qs.max_grant_kb	
			,qs.total_used_grant_kb	
			,qs.last_used_grant_kb	
			,qs.min_used_grant_kb	
			,qs.max_used_grant_kb	
			,qs.total_ideal_grant_kb	
			,qs.last_ideal_grant_kb	
			,qs.min_ideal_grant_kb	
			,qs.max_ideal_grant_kb	
			,qs.total_reserved_threads	
			,qs.last_reserved_threads	
			,qs.min_reserved_threads	
			,qs.max_reserved_threads	
			,qs.total_used_threads	
			,qs.last_used_threads	
			,qs.min_used_threads	
			,qs.max_used_threads'
		else '
			,total_dop=null
			,last_dop=null	
			,min_dop=null	
			,max_dop=null	
			,total_grant_kb=null	
			,last_grant_kb=null	
			,min_grant_kb=null	
			,max_grant_kb=null	
			,total_used_grant_kb=null	
			,last_used_grant_kb=null	
			,min_used_grant_kb=null	
			,max_used_grant_kb=null	
			,total_ideal_grant_kb=null	
			,last_ideal_grant_kb=null	
			,min_ideal_grant_kb=null	
			,max_ideal_grant_kb=null	
			,total_reserved_threads=null	
			,last_reserved_threads=null	
			,min_reserved_threads=null	
			,max_reserved_threads=null	
			,total_used_threads=null	
			,last_used_threads=null	
			,min_used_threads=null	
			,max_used_threads=null'
		end + '

		, delta_worker_time = [dbo].[ufn_sqlwatch_get_delta_value](prev.total_worker_time, qs.total_worker_time)
		, delta_physical_reads = [dbo].[ufn_sqlwatch_get_delta_value](prev.total_physical_reads, qs.total_physical_reads)
		, delta_logical_writes = [dbo].[ufn_sqlwatch_get_delta_value](prev.total_logical_writes, qs.total_logical_writes)
		, delta_logical_reads = [dbo].[ufn_sqlwatch_get_delta_value](prev.total_logical_reads, qs.total_logical_reads)
		, delta_elapsed_time = [dbo].[ufn_sqlwatch_get_delta_value](prev.total_elapsed_time, qs.total_elapsed_time)
		, delta_time_s = case when prev.snapshot_time is null then null else datediff(second, prev.snapshot_time,@snapshot_time) end

	from #s qs

	left join #t prev
		on prev.[sql_instance] = @sql_instance
		and prev.plan_handle = qs.plan_handle
		and prev.statement_start_offset = qs.statement_start_offset
		and prev.statement_end_offset = qs.statement_end_offset
		and prev.[creation_time] = qs.creation_time;
		';

	exec sp_executesql @sql
		, @sql_params
		, @snapshot_time = @snapshot_time
		, @snapshot_type_id = @snapshot_type_id
		, @sql_instance = @sql_instance;
end;
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_logger_requests_and_sessions]
as

	declare @snapshot_type_id tinyint = 30,
			@snapshot_time datetime2(0),
			@sql_instance varchar(32) = dbo.ufn_sqlwatch_get_servername();

	exec [dbo].[usp_sqlwatch_internal_insert_header] 
		@snapshot_time_new = @snapshot_time OUTPUT,
		@snapshot_type_id = @snapshot_type_id;

	insert into dbo.sqlwatch_logger_dm_exec_requests_stats (
		[type]
		, background
		, running
		, runnable
		, sleeping
		, suspended
		, wait_time
		, cpu_time
		, waiting_tasks
		, waiting_tasks_wait_duration_ms
		, snapshot_time
		, snapshot_type_id
		, sql_instance
	)
	select 
		  'type' = case when r.session_id > 50 then 1 else 0 end
		, 'background' = sum(case status when 'Background' then 1 else 0 end)
		-- exclude our own session from counting. This way, if there are no other sessions we can still get a count that shows 0
		-- if we excluded it in the where clause, we would have had a missing for this snapshot time which would have upset dashboards
		, 'running' = sum(case when status = 'Running' and session_id <> @@SPID then 1 else 0 end)
		, 'runnable' = sum(case status when 'Runnable' then 1 else 0 end)
		, 'sleeping' = sum(case status when 'Sleeping' then 1 else 0 end)
		, 'suspended' = sum(case status when 'Suspended' then 1 else 0 end)
		, 'wait_time' = sum(convert(real,wait_time))
		, 'cpu_time' = sum(convert(real,cpu_time))
		, 'waiting_tasks' = isnull(sum(waiting_tasks),0)
		, 'waiting_tasks_wait_duration_ms' = isnull(sum(wait_duration_ms),0)
		, snapshot_time = @snapshot_time
		, snapshot_type_id = @snapshot_type_id
		, sql_instance = @sql_instance
	from sys.dm_exec_requests r (nolock)
	left join (
		-- get waiting tasks
		select type = case when t.session_id > 50 then 1 else 0 end
			, waiting_tasks = count(*)
			, wait_duration_ms = sum(wait_duration_ms)
		from sys.dm_os_waiting_tasks t (nolock)
		where wait_type collate database_default not in (
			select wait_type 
			from dbo.sqlwatch_config_exclude_wait_stats (nolock)
			) 
		and session_id is not null 
		group by case when t.session_id > 50 then 1 else 0 end
	
	) t
	on t.type = case when r.session_id > 50 then 1 else 0 end
	group by case when r.session_id > 50 then 1 else 0 end
	option (keep plan);

	insert into dbo.sqlwatch_logger_dm_exec_sessions_stats (
		  [type]
		, running
		, sleeping
		, dormant
		, preconnect
		, cpu_time
		, reads
		, writes
		, snapshot_time
		, snapshot_type_id
		, sql_instance
	)
	select 
		'type' = is_user_process
		-- exclude our own session from counting. This way, if there are no other sessions we can still get a count that shows 0
		-- if we excluded it in the where clause, we would have had a missing for this snapshot time which would have upset dashboards
		,'running' = sum(case when status = 'Running' and session_id <> @@SPID then 1 else 0 end)
		,'sleeping' = sum(case status when 'Sleeping' then 1 else 0 end)
		,'dormant' = sum(case status when 'Dormant' then 1 else 0 end)
		,'preconnect' = sum(case status when 'Preconnect' then 1 else 0 end)
		,'cpu_time' = sum(convert(real,cpu_time))
		,'reads' = sum(convert(real,reads))
		,'writes' = sum(convert(real,writes))
		, snapshot_time = @snapshot_time
		, snapshot_type_id = @snapshot_type_id
		, sql_instance = @sql_instance
	from sys.dm_exec_sessions (nolock)
	group by is_user_process
	option (keep plan)
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_logger_system_configuration]
AS

/*
-------------------------------------------------------------------------------------------------------------------
 Procedure:
	[usp_sqlwatch_logger_system_configuration]

 Description:
	Log system configuration into tables.

 Parameters
	N/A
	
 Author:
	Fabian Schenker

 Change Log:
	1.0		2020-05-13	- Fabian Schenker, Initial version
-------------------------------------------------------------------------------------------------------------------
*/

set nocount on ;
set xact_abort on;

declare @snapshot_time datetime2(0),
		@snapshot_type_id tinyint = 26,
		@date_snapshot_previous datetime2(0)

select @date_snapshot_previous = max([snapshot_time])
	from [dbo].[sqlwatch_logger_snapshot_header] (nolock) --so we dont get blocked by central repository. this is safe at this point.
	where snapshot_type_id = @snapshot_type_id
	and sql_instance = @@SERVERNAME

	exec [dbo].[usp_sqlwatch_internal_insert_header] 
		@snapshot_time_new = @snapshot_time OUTPUT,
		@snapshot_type_id = @snapshot_type_id


INSERT INTO [dbo].[sqlwatch_logger_system_configuration] (sql_instance, sqlwatch_configuration_id, value, value_in_use, snapshot_time, snapshot_type_id)
SELECT v.sql_instance, m.sqlwatch_configuration_id, v.value, v.value_in_use, @snapshot_time, @snapshot_type_id
  FROM dbo.vw_sqlwatch_sys_configurations v
 INNER JOIN dbo.[sqlwatch_meta_system_configuration] m
    ON v.configuration_id = m.configuration_id
   AND v.sql_instance = m.sql_instance


-- Slowly Changing Dimension for System Configuration

-- Set valid_until for changed or deleted:
UPDATE curr
   SET curr.valid_until = @snapshot_time
  FROM [dbo].[sqlwatch_meta_system_configuration_scd] curr
  LEFT JOIN (SELECT v.sql_instance, m.sqlwatch_configuration_id, v.value, v.value_in_use
               FROM dbo.vw_sqlwatch_sys_configurations v
              INNER JOIN dbo.[sqlwatch_meta_system_configuration] m
                 ON v.configuration_id = m.configuration_id
                AND v.sql_instance = m.sql_instance) n
   ON curr.sql_instance = n.sql_instance
  AND curr.sqlwatch_configuration_id = n.sqlwatch_configuration_id
 WHERE n.sql_instance IS NULL OR curr.value <> n.value OR curr.value_in_use <> n.value_in_use

-- Add the new ones or the changed:
INSERT INTO [dbo].[sqlwatch_meta_system_configuration_scd] (sql_instance, sqlwatch_configuration_id, value, value_in_use, valid_from, valid_until)
SELECT DISTINCT v.sql_instance, m.sqlwatch_configuration_id, v.value, v.value_in_use, @snapshot_time, NULL
  FROM dbo.vw_sqlwatch_sys_configurations v
 INNER JOIN dbo.[sqlwatch_meta_system_configuration] m
    ON v.configuration_id = m.configuration_id
   AND v.sql_instance = m.sql_instance
 LEFT JOIN [dbo].[sqlwatch_meta_system_configuration_scd] curr
   ON curr.sql_instance = v.sql_instance
  AND curr.sqlwatch_configuration_id = m.sqlwatch_configuration_id
WHERE curr.sql_instance IS NULL OR curr.value <> v.value OR curr.value_in_use <> v.value_in_use
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_logger_whoisactive] (
	@min_session_duration_seconds smallint = 15
	)
AS

set xact_abort on;

	declare @sp_whoisactive_destination_table varchar(255),
			@snapshot_time datetime,
			@snapshot_type_id tinyint  = 11;

	--------------------------------------------------------------------------------------------------------------
	-- sp_whoisactive
	-- Please download and install The Great sp_whoisactive from http://whoisactive.com/ and thank Adam Machanic 
	-- for the numerous times sp_whoisactive saved our backs.
	-- an alternative approach would be to use the SQL deadlock monitor and service broker to record blocking
	-- or deadlocked transactions into a table -- or XE to save to xml - but this could cause trouble parsing large
	-- xmls.
	--------------------------------------------------------------------------------------------------------------
	if object_id('master.dbo.sp_WhoIsActive') is not null or object_id('dbo.sp_WhoIsActive') is not null
		begin
			create table [##SQLWATCH_7A2124DA-B485-4C43-AE04-65D61E6A157C] (
				[snapshot_time] datetime2(0) not null
				,[start_time] datetime NOT NULL
				,[session_id] smallint NOT NULL
				,[status] varchar(30) NOT NULL
				,[percent_complete] varchar(30) NULL
				,[host_name] nvarchar(128) NULL
				,[database_name] nvarchar(128) NULL
				,[program_name] nvarchar(128) NULL
				,[sql_text] xml NULL,[sql_command] xml NULL
				,[login_name] nvarchar(128) NOT NULL
				,[open_tran_count] varchar(30) NULL
				,[wait_info] nvarchar(4000) NULL
				,[blocking_session_id] smallint NULL
				,[blocked_session_count] varchar(30) NULL
				,[CPU] varchar(30) NULL
				,[used_memory] varchar(30) NULL
				,[tempdb_current] varchar(30) NULL
				,[tempdb_allocations] varchar(30) NULL
				,[reads] varchar(30) NULL
				,[writes] varchar(30) NULL
				,[physical_reads] varchar(30) NULL
				,[login_time] datetime NULL
				)
				;
			-- we are running WhoIsActive is very lightweight mode without any additional info and without execution plans
			exec dbo.sp_WhoIsActive
				 @get_outer_command = 1
				,@output_column_list = '[collection_time][start_time][session_id][status][percent_complete][host_name][database_name][program_name][sql_text][sql_command][login_name][open_tran_count][wait_info][blocking_session_id][blocked_session_count][CPU][used_memory][tempdb_current][tempdb_allocations][reads][writes][physical_reads][login_time]'
				,@find_block_leaders = 1
				,@destination_table = [##SQLWATCH_7A2124DA-B485-4C43-AE04-65D61E6A157C];
			-- the insert to tmp then actual table approach is required mainly to use our
			-- snapshot_time and enforce referential integrity with the header table and
			-- to apply any additional filtering:

			exec [dbo].[usp_sqlwatch_internal_insert_header] 
				@snapshot_time_new = @snapshot_time OUTPUT,
				@snapshot_type_id = @snapshot_type_id;

			insert into [dbo].[sqlwatch_logger_whoisactive] ([snapshot_time],[start_time],
					 [session_id],[status],[percent_complete],[host_name]
					,[database_name],[program_name],[sql_text],[sql_command],[login_name]
					,[open_tran_count],[wait_info],[blocking_session_id],[blocked_session_count]
					,[CPU],[used_memory],[tempdb_current],[tempdb_allocations],[reads]
					,[writes],[physical_reads],[login_time],[snapshot_type_id],[sql_instance])
			select   [snapshot_time] = @snapshot_time
					,[start_time],[session_id],[status],[percent_complete],[host_name]
					,[database_name],[program_name],[sql_text],[sql_command],[login_name]
					,[open_tran_count],[wait_info],[blocking_session_id],[blocked_session_count]
					,[CPU],[used_memory],[tempdb_current],[tempdb_allocations],[reads]
					,[writes],[physical_reads],[login_time], @snapshot_type_id, @@SERVERNAME
			from [##SQLWATCH_7A2124DA-B485-4C43-AE04-65D61E6A157C]
			-- exclude anything that has been running for less that the desired duration in seconds (default 15)
			where [start_time] < dateadd(s, -@min_session_duration_seconds,getdate())
			-- unless its being blocked or is a blocker
			or [blocking_session_id] is not null or [blocked_session_count] > 0;
		end;
	else
		begin
				exec [dbo].[usp_sqlwatch_internal_log]
					@proc_id = @@PROCID,
					@process_stage = '9EB9405D-C924-4E92-88E1-1CB5E24F3733',
					@process_message = 'sp_WhoIsActive is not found',
					@process_message_type = 'WARNING';
		end;
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_logger_xes_blockers]
AS

set nocount on;
set xact_abort on;

declare @execution_count bigint = 0,
		@session_name nvarchar(64) = 'SQLWATCH_blockers',
		@snapshot_time datetime,
		@snapshot_type_id tinyint = 9,
		@filename varchar(8000),
		@sql_instance varchar(32) = dbo.ufn_sqlwatch_get_servername();

declare @event_data utype_event_data;

--quit if the collector is switched off
if (select collect 
	from [dbo].[sqlwatch_config_snapshot_type]
	where snapshot_type_id = @snapshot_type_id
	) = 0
	begin
		return;
	end;

exec [dbo].[usp_sqlwatch_internal_insert_header] 
	@snapshot_time_new = @snapshot_time OUTPUT,
	@snapshot_type_id = @snapshot_type_id;

begin tran;

	insert into @event_data
	exec [dbo].[usp_sqlwatch_internal_get_xes_data]
		@session_name = @session_name;

	--bail out of no xes data to process:
	if not exists (select top 1 * from @event_data)
		begin
			commit tran;
			return;
		end;


/*  For this to work you must enable blocked process monitor */

/*  The below code, whilst not directly copied, is inspired by and based on Michael J Stewart blocked process report.
	I have learned how to approach this problem from Michael's blog. Please add his blog to your favourites as its a really good SQL Server Knowledgebase.

	http://michaeljswart.com/2016/02/look-at-blocked-process-reports-collected-with-extended-events/
	https://github.com/mjswart/sqlblockedprocesses licensed under MIT
	https://github.com/mjswart/sqlblockedprocesses/blob/master/LICENSE

	MIT License

	Copyright (c) 2018 mjswart

	Permission is hereby granted, free of charge, to any person obtaining a copy
	of this software and associated documentation files (the "Software"), to deal
	in the Software without restriction, including without limitation the rights
	to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
	copies of the Software, and to permit persons to whom the Software is
	furnished to do so, subject to the following conditions:

	The above copyright notice and this permission notice shall be included in all
	copies or substantial portions of the Software.

	THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
	IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
	FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
	AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
	LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
	OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
	SOFTWARE.	
				
*/


begin try
	insert into dbo.sqlwatch_logger_xes_blockers (
				[monitor_loop]
			, [lockMode]
			, [blocked_spid]
			, [blocked_ecid]
			, [blocked_clientapp]
			, [blocked_currentdbname]
			, [blocked_hostname]
			, [blocked_loginname]
			, [blocked_inputbuff]
			, [blocking_spid]
			, [blocking_ecid]
			, [blocking_clientapp]
			, [blocking_currentdbname]
			, [blocking_hostname]
			, [blocking_loginname]
			, [blocking_inputbuff]
			, [event_time]
			, [blocking_duration_ms]
			, [report_xml]
			, [snapshot_time]
			, snapshot_type_id
			, sql_instance
	)
				
	select
				[blocked_process_report_details].[monitor_loop]
			, [blocked_process_report_details].[lockMode]
			, [blocked_process_report_details].[blocked_spid]
			, [blocked_process_report_details].[blocked_ecid]
			, [blocked_clientapp] = [dbo].[ufn_sqlwatch_parse_job_name]([blocked_process_report_details].[blocked_clientapp], null)
			, [blocked_process_report_details].[blocked_currentdbname]
			, [blocked_process_report_details].[blocked_hostname]
			, [blocked_process_report_details].[blocked_loginname]
			, [blocked_process_report_details].[blocked_inputbuff]
			, [blocked_process_report_details].[blocking_spid]
			, [blocked_process_report_details].[blocking_ecid]
			, [blocking_clientapp] = [dbo].[ufn_sqlwatch_parse_job_name]([blocked_process_report_details].[blocking_clientapp],null)
			, [blocked_process_report_details].[blocking_currentdbname]
			, [blocked_process_report_details].[blocking_hostname]
			, [blocked_process_report_details].[blocking_loginname]
			, [blocked_process_report_details].[blocking_inputbuff]

			, [bp_report_xml].[event_date]
			, convert(real,[bp_report_xml].[blocking_duration_ms])
			, [bp_report_xml].[bp_report_xml]	

			, [snapshot_time] = @snapshot_time
			, snapshot_type_id = @snapshot_type_id
			, sql_instance = @sql_instance
	from @event_data xet

	cross apply ( 
		select 
		xet.event_data 
	) AS event_data ([xml])

	cross apply  (
		select
				-- extract blocked process xml contained in the event session xml
				event_date = event_data.[xml].value('(event/@timestamp)[1]', 'datetime')
			, blocking_duration_ms = event_data.[xml].value('(//event/data[@name="duration"]/value)[1]', 'bigint')/1000
			, bp_report_xml = event_data.[xml].query('//event/data/value/blocked-process-report')
	) as bp_report_xml

	cross apply (
		select 

				-- generic
				[monitor_loop] = bp_report_xml.value('(//@monitorLoop)[1]', 'nvarchar(100)')
			, [lockMode]= bp_report_xml.value('(./blocked-process-report/blocked-process/process/@lockMode)[1]', 'nvarchar(128)')
						  
				-- blocked-process-report
			, [blocked_spid] = bp_report_xml.value('(./blocked-process-report/blocked-process/process/@spid)[1]', 'int')
			, [blocked_ecid] = bp_report_xml.value('(./blocked-process-report/blocked-process/process/@ecid)[1]', 'int')
			, [blocked_clientapp] = bp_report_xml.value('(./blocked-process-report/blocked-process/process/@clientapp)[1]', 'nvarchar(128)')
			, [blocked_currentdbname] = nullif(bp_report_xml.value('(./blocked-process-report/blocked-process/process/@currentdbname)[1]', 'nvarchar(128)'),'')
			, [blocked_hostname] = nullif(bp_report_xml.value('(./blocked-process-report/blocked-process/process/@hostname)[1]', 'nvarchar(128)'),'')
			, [blocked_loginname] = nullif(bp_report_xml.value('(./blocked-process-report/blocked-process/process/@loginname)[1]', 'nvarchar(128)'),'')
			, [blocked_inputbuff] = nullif(bp_report_xml.value('(./blocked-process-report/blocked-process/process/inputbuf)[1]', 'nvarchar(max)'),'')
						  
				-- blocking-process
			, [blocking_spid] = bp_report_xml.value('(./blocked-process-report/blocking-process/process/@spid)[1]', 'int')
			, [blocking_ecid] = bp_report_xml.value('(./blocked-process-report/blocking-process/process/@ecid)[1]', 'int')
			, [blocking_clientapp] = bp_report_xml.value('(./blocked-process-report/blocking-process/process/@clientapp)[1]', 'nvarchar(128)')
			, [blocking_currentdbname] = nullif(bp_report_xml.value('(./blocked-process-report/blocking-process/process/@currentdbname)[1]', 'nvarchar(128)'),'')
			, [blocking_hostname] = nullif(bp_report_xml.value('(./blocked-process-report/blocking-process/process/@hostname)[1]', 'nvarchar(128)'),'')
			, [blocking_loginname] = nullif(bp_report_xml.value('(./blocked-process-report/blocking-process/process/@loginname)[1]', 'nvarchar(128)'),'')
			, [blocking_inputbuff] = nullif(bp_report_xml.value('(./blocked-process-report/blocking-process/process/inputbuf)[1]', 'nvarchar(max)'),'')
						  
		) as blocked_process_report_details

	left join dbo.sqlwatch_logger_xes_blockers b
	on b.event_time = [bp_report_xml].[event_date]
	and b.monitor_loop = [blocked_process_report_details].[monitor_loop]
	and b.[blocked_spid] = [blocked_process_report_details].[blocked_spid]
	and b.[blocked_ecid] = [blocked_process_report_details].[blocked_ecid]
	and b.[blocking_spid] = [blocked_process_report_details].[blocking_spid]
	and b.[blocking_ecid] = [blocked_process_report_details].[blocking_ecid]


	where [blocked_process_report_details].[blocking_spid] is not null
	and [blocked_process_report_details].[blocked_spid] is not null

	-- skip existing rows:
	and b.monitor_loop is null
	and b.event_time is null
	and b.blocked_spid is null

	option (maxdop 1, keep plan);

	commit tran

end try
begin catch
	if @@TRANCOUNT > 0
		begin
			rollback tran;
		end;

		exec [dbo].[usp_sqlwatch_internal_log]
			@proc_id = @@PROCID,
			@process_stage = 'F774F15F-C0F1-4C77-835D-C3EE6451F831',
			@process_message = null,
			@process_message_type = 'ERROR';
end catch;
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_logger_xes_diagnostics]
AS

set xact_abort on;
set nocount on;

declare @snapshot_type_id tinyint = 7,
		@snapshot_time datetime2(0),
		@target_data_char nvarchar(max),
		@target_data_xml xml,
		@max_event_time datetime;

declare @execution_count bigint = 0,
		@session_name nvarchar(64) = 'system_health',
		@address varbinary(8),
		@filename varchar(8000),
		@sql_instance varchar(32) = dbo.ufn_sqlwatch_get_servername(),
		@store_event_data smallint = dbo.ufn_sqlwatch_get_config_value(23,null),
		@last_event_time datetime;

declare @event_data utype_event_data;

--bail out if this snapshot is set to not be collected:
if (select collect 
	from [dbo].[sqlwatch_config_snapshot_type]
	where snapshot_type_id = @snapshot_type_id
	) = 0
	begin
		return;
	end;

exec [dbo].[usp_sqlwatch_internal_insert_header] 
	@snapshot_time_new = @snapshot_time OUTPUT,
	@snapshot_type_id = @snapshot_type_id;

begin transaction;

	insert into @event_data
	exec [dbo].[usp_sqlwatch_internal_get_xes_data]
		@session_name = @session_name,
		@object_name = 'sp_server_diagnostics_component_result',
		@min_interval_s = 300;

	--bail out of no xes data to process:
	if not exists (select top 1 * from @event_data)
		begin
			commit transaction;
			return;
		end;

	select @max_event_time = max(event_time) 
	from [dbo].[sqlwatch_logger_xes_query_processing]
	where sql_instance = @sql_instance;

	set @max_event_time = isnull(@max_event_time,'1970-01-01');

	with cte_query_processing as (
		select 
			[event_time] =  xed.event_data.value('(@timestamp)[1]', 'datetime'),
			[max_workers] = report_xml_node.value('(./@maxWorkers)[1]','bigint'),
			[workers_created] = report_xml_node.value('(./@workersCreated)[1]','bigint'),
			[idle_workers] = report_xml_node.value('(./@workersIdle)[1]','bigint'),
			[pending_tasks] = report_xml_node.value('(./@pendingTasks)[1]','bigint'),
			[unresolvable_deadlocks] = report_xml_node.value('(./@hasUnresolvableDeadlockOccurred)[1]','int'),
			[deadlocked_scheduler] = report_xml_node.value('(./@hasDeadlockedSchedulersOccurred)[1]','int'),
			[snapshot_time] = @snapshot_time,
			[snapshot_type_id] = @snapshot_type_id
		from @event_data t
		cross apply t.event_data.nodes('event') as xed (event_data)
		cross apply xed.event_data.nodes('./data[@name="data"]/value/queryProcessing') AS report_xml_nodes(report_xml_node)
	)
	insert into [dbo].[sqlwatch_logger_xes_query_processing](
			event_time
		, max_workers
		, workers_created
		, idle_workers
		, pending_tasks
		, unresolvable_deadlocks
		, deadlocked_scheduler
		, snapshot_time
		, snapshot_type_id
		)
	select 
		  [event_time]
		, [max_workers]
		, [workers_created]
		, [idle_workers]
		, [pending_tasks]
		, [unresolvable_deadlocks]
		, [deadlocked_scheduler]
		, [snapshot_time]
		, [snapshot_type_id]
	from cte_query_processing
	where event_time > @max_event_time
	option (maxdop 1, keepfixed plan);

	select @max_event_time = max(event_time) 
	from [dbo].[sqlwatch_logger_xes_iosubsystem]
	where sql_instance = @sql_instance;

	set @max_event_time = isnull(@max_event_time,'1970-01-01');

	with cte_io_subsystem as (
		select
			[event_time] = xed.event_data.value('(@timestamp)[1]', 'datetime'),
			[io_latch_timeouts] = report_xml_node.value('(./@ioLatchTimeouts)[1]','bigint'),
			[total_long_ios] = report_xml_node.value('(./@totalLongIos)[1]','bigint'),
			[longest_pending_request_file] = report_xml_node.value('(./longestPendingRequests/pendingRequest/@filePath)[1]','varchar(255)'),
			[longest_pending_request_duration] = report_xml_node.value('(./longestPendingRequests/pendingRequest/@duration)[1]','bigint'),
			[snapshot_time] = @snapshot_time,
			[snapshot_type_id] = @snapshot_type_id
		from @event_data t
		cross apply t.event_data.nodes('event') as xed (event_data)
		cross apply xed.event_data.nodes('./data[@name="data"]/value/ioSubsystem') AS report_xml_nodes(report_xml_node)
	)

	insert into [dbo].[sqlwatch_logger_xes_iosubsystem](
		  event_time
		, io_latch_timeouts
		, total_long_ios
		, longest_pending_request_file
		, longest_pending_request_duration
		, snapshot_time
		, snapshot_type_id
		)
	select 
		  [event_time]
		, [io_latch_timeouts]
		, [total_long_ios]
		, [longest_pending_request_file]
		, [longest_pending_request_duration]
		, [snapshot_time]
		, [snapshot_type_id] 
	from cte_io_subsystem
	where event_time > @max_event_time
	option (maxdop 1, keepfixed plan);

commit transaction;
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_logger_xes_long_queries]
AS

set nocount on;

declare @snapshot_type_id tinyint = 7,
		@snapshot_time datetime2(0),
		@target_data_char nvarchar(max),
		@target_data_xml xml;

declare @session_name nvarchar(64) = 'SQLWATCH_long_queries',
		@sql_instance varchar(32) = dbo.ufn_sqlwatch_get_servername(),
		@store_event_data smallint = dbo.ufn_sqlwatch_get_config_value(23,null);

declare @event_data utype_event_data;

--quit if the collector is switched off
if (select collect 
	from [dbo].[sqlwatch_config_snapshot_type]
	where snapshot_type_id = @snapshot_type_id
	) = 0
	begin
		return;
	end;

exec [dbo].[usp_sqlwatch_internal_insert_header] 
	@snapshot_time_new = @snapshot_time OUTPUT,
	@snapshot_type_id = @snapshot_type_id;

begin tran;

	insert into @event_data
	exec [dbo].[usp_sqlwatch_internal_get_xes_data]
		@session_name = @session_name;

	--bail out of no xes data to process:
	if not exists (select top 1 * from @event_data)
		begin
			commit tran;
			return;
		end;

	SELECT 
			attach_activity_id=left(xed.event_data.value('(action[@name="attach_activity_id"]/value )[1]', 'varchar(255)'),36) --discard sequence
		,[event_time]=xed.event_data.value('(@timestamp)[1]', 'datetime')
		,[event_name]=xed.event_data.value('(@name)[1]', 'varchar(255)')
		,[session_id]=isnull(xed.event_data.value('(action[@name="session_id"]/value)[1]', 'bigint'),0)
		,[database_name]=xed.event_data.value('(action[@name="database_name"]/value)[1]', 'varchar(255)')
		,[cpu_time]=xed.event_data.value('(data[@name="cpu_time"]/value)[1]', 'bigint')
		,[physical_reads]=xed.event_data.value('(data[@name="physical_reads"]/value)[1]', 'bigint')
		,[logical_reads]=xed.event_data.value('(data[@name="logical_reads"]/value)[1]', 'bigint')
		,[writes]=xed.event_data.value('(data[@name="writes"]/value)[1]', 'bigint')
		,[spills]=xed.event_data.value('(data[@name="spills"]/value)[1]', 'bigint')
		,[offset_start]=xed.event_data.value('(data[@name="offset"]/value)[1]', 'bigint')
		,[offset_end]=xed.event_data.value('(data[@name="offset_end"]/value)[1]', 'bigint')
		,[username]=xed.event_data.value('(action[@name="username"]/value)[1]', 'varchar(255)')
		--,[object_name]=nullif(xed.event_data.value('(data[@name="object_name"]/value)[1]', 'varchar(max)'),'')
		,[client_hostname]=xed.event_data.value('(action[@name="client_hostname"]/value)[1]', 'varchar(255)')
		,[client_app_name]=xed.event_data.value('(action[@name="client_app_name"]/value)[1]', 'varchar(255)')
		,[duration_ms]=xed.event_data.value('(data[@name="duration"]/value)[1]', 'bigint')/1000
		,[wait_type]=xed.event_data.value('(data[@name="wait_type"]/text )[1]', 'varchar(255)')
		,[plan_handle] = convert(varbinary(64),'0x' + xed.event_data.value('(action[@name="plan_handle"]/value)[1]', 'varchar(max)'),1)

		--for best performance, sql text exclusions could be baked into the xes so we do not even collect anything we do not want
		--this would require dynamic xes creation which is something I will look to do in the future
		,[sql_text] = xed.event_data.value('(action[@name="sql_text"]/value)[1]', 'varchar(max)')
		,sql_instance = @sql_instance
		,event_data = case when @store_event_data = 1 then t.event_data else null end
	into #t_queries
	from @event_data t
		cross apply t.event_data.nodes('event') as xed (event_data);

	create nonclustered index idx_tmp_t_queries_1 on #t_queries (sql_instance, plan_handle, [offset_start], [offset_end]);
	create nonclustered index idx_tmp_t_queries_2 on #t_queries (event_name, event_time, session_id, sql_instance);

	delete from #t_queries
	where plan_handle = 0x0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000
	or offset_start is null
	or offset_end is null;
					
	--normalise query text and plans
	declare @plan_handle_table utype_plan_handle
	insert into @plan_handle_table (plan_handle, statement_start_offset, statement_end_offset )
	select distinct plan_handle,  offset_start, offset_end
	from #t_queries;

	declare @sqlwatch_plan_id dbo.utype_plan_id
	insert into @sqlwatch_plan_id 
	exec [dbo].[usp_sqlwatch_internal_get_query_plans]
		@plan_handle = @plan_handle_table, 
		@sql_instance = @sql_instance
	;
					
	exec [dbo].[usp_sqlwatch_internal_insert_header] 
		@snapshot_time_new = @snapshot_time OUTPUT,
		@snapshot_type_id = @snapshot_type_id;

	begin try
		set xact_abort on;

		insert into dbo.[sqlwatch_logger_xes_long_queries] (
				[event_time], event_name, session_id, sqlwatch_database_id
			, cpu_time, physical_reads, logical_reads, writes, spills
			, username
			, client_hostname, client_app_name
			, duration_ms
			, snapshot_time, snapshot_type_id 
			, plan_handle
			, statement_start_offset
			, statement_end_offset
			, attach_activity_id
			, sql_instance
			, event_data
			)

		select 
				tx.[event_time], tx.event_name, tx.session_id, db.sqlwatch_database_id
			, tx.cpu_time, tx.physical_reads, tx.logical_reads, tx.writes, tx.spills
			, tx.username
			, tx.client_hostname
			, client_app_name = [dbo].[ufn_sqlwatch_parse_job_name] ( tx.client_app_name, j.name )
			, tx.duration_ms
			, [snapshot_time] = @snapshot_time
			, [snapshot_type_id] = @snapshot_type_id
			, tx.plan_handle
			, tx.[offset_start]
			, tx.[offset_end]
			, tx.attach_activity_id
			, tx.sql_instance
			, tx.event_data
		from #t_queries tx

		inner join dbo.sqlwatch_meta_database db
			on db.database_name = tx.database_name
			and db.is_current = 1

		-- do not load queries that we arleady have
		left join dbo.[sqlwatch_logger_xes_long_queries] x
			on x.event_name = tx.event_name
			and x.event_time = tx.event_time
			and x.session_id = tx.session_id
			and x.sql_instance = tx.sql_instance

		left join msdb.dbo.sysjobs j
			on j.job_id = [dbo].[ufn_sqlwatch_parse_job_id] ( tx.client_app_name )

		-- exclude queries containing text that we do not want to collect or coming from an excluded host or an application
		left join [dbo].[sqlwatch_config_exclude_xes_long_query] ex
			on case when ex.sql_text is not null then tx.sql_text else '%' end like isnull(ex.sql_text,'%')
			and case when ex.client_app_name is not null then tx.client_app_name else '%' end like isnull(ex.client_app_name,'%')
			and case when ex.client_hostname is not null then tx.client_hostname else '%' end like isnull(ex.client_hostname,'%')
			and case when ex.username is not null then tx.username else '%' end like isnull(ex.username,'%')

		where ex.[exclusion_id] is null
		and x.event_time is null;

		commit transaction;

	end try
	begin catch

		if @@TRANCOUNT > 0
			begin
				rollback tran;
			end

			exec [dbo].[usp_sqlwatch_internal_log]
				@proc_id = @@PROCID,
				@process_stage = 'D3D0A427-8CD8-4CBC-BB35-FE872A728704',
				@process_message = null,
				@process_message_type = 'ERROR';
	end catch;
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_logger_xes_query_problems]
as


set nocount on

/*

THIS IS NOT YET READY AS THE XES NEEDS MORE WORK

declare @snapshot_time datetime2(0),
		@snapshot_type_id tinyint = 6

declare @execution_count bigint = 0,
		@session_name nvarchar(64) = 'SQLWATCH_query_problems',
		@address varbinary(8),
		@filename varchar(8000),
		@sql_instance varchar(32) = dbo.ufn_sqlwatch_get_servername(),
		@store_event_data smallint = dbo.ufn_sqlwatch_get_config_value(23,null),
		@last_event_time datetime;;

declare @event_data utype_event_data;

--quit if the collector is switched off
if (select collect 
	from [dbo].[sqlwatch_config_snapshot_type]
	where snapshot_type_id = @snapshot_type_id
	) = 0
	begin
		return;
	end;

exec [dbo].[usp_sqlwatch_internal_insert_header] 
	@snapshot_time_new = @snapshot_time OUTPUT,
	@snapshot_type_id = @snapshot_type_id;

begin tran;

	insert into @event_data
	exec [dbo].[usp_sqlwatch_internal_get_xes_data]
		@session_name = @session_name;

	--bail out of no xes data to process:
	if not exists (select top 1 * from @event_data)
		begin
			commit tran;
			return;
		end;


--quit of the collector is switched off
if (select collect from [dbo].[sqlwatch_config_snapshot_type]
	where snapshot_type_id = @snapshot_type_id) = 0
		begin
			return
		end;

SELECT 
	 [event_time]=xed.event_data.value('(@timestamp)[1]', 'datetime')
	,[event_name]=xed.event_data.value('(@name)[1]', 'varchar(255)')
	,[username]=xed.event_data.value('(action[@name="username"]/value)[1]', 'varchar(255)')
	--,[sql_text]=xed.event_data.value('(action[@name="sql_text"]/value)[1]', 'varchar(max)')
	,[client_hostname]=xed.event_data.value('(action[@name="client_hostname"]/value)[1]', 'varchar(255)')
	,[client_app_name]=xed.event_data.value('(action[@name="client_app_name"]/value)[1]', 'varchar(255)')
	,[problem_details] = t.event_data
	,[event_hashbytes]
	,occurence
into #t_queries
from @event_data t
	cross apply t.event_data.nodes('event') as xed (event_data)
	where xed.event_data.value('(@name)[1]', 'varchar(255)') <> 'query_post_execution_showplan';
	
insert into dbo.[sqlwatch_logger_xes_query_problems] (
		[event_time], event_name, username
	, client_hostname, client_app_name
	, snapshot_time, snapshot_type_id, sql_instance, [problem_details], [event_hash], occurence)

select 
		tx.[event_time], tx.event_name, tx.username
	, tx.client_hostname, tx.client_app_name
	,[snapshot_time] = @snapshot_time
	,[snapshot_type_id] = @snapshot_type_id
	,sql_instance = @sql_instance
	,tx.[problem_details]
	,tx.[event_hashbytes]
	,occurence = o.occurence
from #t_queries tx

-- do not load queries that we arleady have
left join dbo.[sqlwatch_logger_xes_query_problems] x
	on x.[event_hash] = tx.[event_hashbytes]
	and x.event_time = tx.event_time
	and x.event_name = tx.event_name

outer apply (
	select occurence=max(occurence)
	from #t_queries
	where [event_hashbytes] = tx.[event_hashbytes]
) o

where tx.occurence = 1 
and x.[event_hash] is null;

*/
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_logger_xes_waits]
AS


set nocount on

declare @snapshot_time datetime2(0),
		@snapshot_type_id tinyint = 6

declare @execution_count bigint = 0,
		@session_name nvarchar(64) = 'SQLWATCH_waits',
		@address varbinary(8),
		@filename varchar(8000),
		@sql_instance varchar(32) = dbo.ufn_sqlwatch_get_servername(),
		@store_event_data smallint = dbo.ufn_sqlwatch_get_config_value(23,null),
		@last_event_time datetime;;

declare @event_data utype_event_data;

--quit if the collector is switched off
if (select collect 
	from [dbo].[sqlwatch_config_snapshot_type]
	where snapshot_type_id = @snapshot_type_id
	) = 0
	begin
		return;
	end;

exec [dbo].[usp_sqlwatch_internal_insert_header] 
	@snapshot_time_new = @snapshot_time OUTPUT,
	@snapshot_type_id = @snapshot_type_id;

begin tran;

	insert into @event_data
	exec [dbo].[usp_sqlwatch_internal_get_xes_data]
		@session_name = @session_name;

	--bail out of no xes data to process:
	if not exists (select top 1 * from @event_data)
		begin
			commit tran;
			return;
		end;


	select
		[event_time] = xed.event_data.value('(@timestamp)[1]', 'datetime'),
		[wait_type] = xed.event_data.value('(data[@name="wait_type"]/text)[1]', 'varchar(255)'),
		[duration] = xed.event_data.value('(data[@name="duration"]/value)[1]', 'bigint'),
		[signal_duration] = xed.event_data.value('(data[@name="signal_duration"]/value)[1]', 'bigint'),
		[activity_id] = xed.event_data.value('(action[@name="attach_activity_id"]/value)[1]', 'varchar(255)'),
		--[query_hash] = xed.event_data.value('(action[@name="query_hash"]/value)[1]', 'decimal(20,0)'),
		[session_id] = xed.event_data.value('(action[@name="session_id"]/value)[1]', 'int'),
		[username] = isnull(xed.event_data.value('(action[@name="username"]/value)[1]', 'varchar(255)'),xed.event_data.value('(action[@name="session_nt_username"]/value)[1]', 'varchar(255)')),
		--[sql_text] = xed.event_data.value('(action[@name="sql_text"]/value)[1]', 'varchar(max)'),
		[database_name] = xed.event_data.value('(action[@name="database_name"]/value)[1]', 'varchar(255)'),
		[client_hostname] = xed.event_data.value('(action[@name="client_hostname"]/value)[1]', 'varchar(255)'),
		[client_app_name] = xed.event_data.value('(action[@name="client_app_name"]/value)[1]', 'varchar(255)'),
		[plan_handle] = convert(varbinary(64),'0x' + xed.event_data.value('(action[@name="plan_handle"]/value)[1]', 'varchar(max)'),1),
		offset_start = frame.event_data.value('(@offsetStart)[1]', 'varchar(255)'),
		offset_end = frame.event_data.value('(@offsetEnd)[1]', 'varchar(255)'),
		[sql_handle] = convert(varbinary(64),frame.event_data.value('(@handle)[1]', 'varchar(255)'),1),
		sql_instance = @sql_instance,
		event_data =  case when @store_event_data = 1 then t.event_data else null end
	into #w
	from @event_data t
	cross apply t.event_data.nodes('event') as xed (event_data)
	cross apply xed.event_data.nodes('//frame') as frame (event_data)
			
	-- exclude any waits we dont want to collect:
	where xed.event_data.value('(data[@name="wait_type"]/text)[1]', 'varchar(255)') not in (
		select wait_type from sqlwatch_config_exclude_wait_stats
	)
	option (maxdop 1, keep plan);

	create nonclustered index idx_tmp_w on #w ( wait_type, sql_instance, event_time, session_id, activity_id );

	delete from #w
	where plan_handle = 0x0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000
	or offset_start is null
	or offset_end is null;

	--normalise query text and plans
	declare @plan_handle_table dbo.utype_plan_handle
	insert into @plan_handle_table (plan_handle, statement_start_offset, statement_end_offset, [sql_handle] )
	select distinct plan_handle,  offset_start, offset_end, [sql_handle]
	from #w
	;

	declare @sqlwatch_plan_id dbo.utype_plan_id
	insert into @sqlwatch_plan_id 
	exec [dbo].[usp_sqlwatch_internal_get_query_plans]
		@plan_handle = @plan_handle_table, 
		@sql_instance = @sql_instance
	;

	exec [dbo].[usp_sqlwatch_internal_insert_header] 
		@snapshot_time_new = @snapshot_time OUTPUT,
		@snapshot_type_id = @snapshot_type_id
	;

	begin try

		insert into [dbo].[sqlwatch_logger_xes_wait_event] (
					event_time
				, wait_type_id
				, duration
				, signal_duration
				, session_id
				, username
				, client_hostname
				, client_app_name
				, plan_handle
				, statement_start_offset
				, statement_end_offset
				, sql_instance
				, snapshot_time
				, snapshot_type_id
				, activity_id
				, event_data
				)
		select 
				w.event_time
			, s.wait_type_id
			, w.duration
			, w.signal_duration
			, w.session_id
			, w.username
			, w.client_hostname
			, client_app_name = [dbo].[ufn_sqlwatch_parse_job_name] ( w.client_app_name, j.name )
			, w.plan_handle
			, w.offset_start
			, w.offset_end
			, w.sql_instance
			, snapshot_time = @snapshot_time
			, snapshot_type_id = @snapshot_type_id
			, w.activity_id
			, w.event_data
		from #w w
			
		inner join dbo.sqlwatch_meta_wait_stats s
			on s.wait_type = w.wait_type
			and s.sql_instance = w.sql_instance
		
		left join msdb.dbo.sysjobs j
			on j.job_id = [dbo].[ufn_sqlwatch_parse_job_id] (client_app_name )
			
		left join [dbo].[sqlwatch_logger_xes_wait_event] t
			on t.event_time = w.event_time
			and t.session_id = w.session_id
			and t.sql_instance = w.sql_instance
			and s.wait_type = w.wait_type
			and w.activity_id = t.activity_id

		where t.event_time is null;

		commit transaction;

	end try
	begin catch
		if @@TRANCOUNT > 0
			rollback transaction;

			exec [dbo].[usp_sqlwatch_internal_log]
				@proc_id = @@PROCID,
				@process_stage = 'D3D0A427-8CD8-4CBC-BB35-FE872A728704',
				@process_message = null,
				@process_message_type = 'ERROR';
	end catch;
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_purge_orphaned_snapshots]
as

declare @sql varchar(max) = ''

select @sql = @sql + 'delete l from ' + TABLE_NAME + ' l
left join [dbo].[sqlwatch_logger_snapshot_header] h
on h.sql_instance = l.sql_instance
and h.snapshot_type_id = l.snapshot_type_id
and h.snapshot_time = l.snapshot_time
where h.snapshot_time is null;'
from INFORMATION_SCHEMA.TABLES
where TABLE_NAME like '%logger%'
and TABLE_TYPE = 'BASE TABLE'

exec (@sql)
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_repository_get_remote_data]
	@sql nvarchar(max),
	@sql_instance varchar(32)
AS
BEGIN

	--set xact_abort on;
	--set nocount on;
	
	--declare @ls_server nvarchar(max),
	--		@table_name nvarchar(max),
	--		@table_schema nvarchar(max),
	--		@sql_1 nvarchar(max),
	--		@hostname nvarchar(max),
	--		@error_message nvarchar(max),
	--		@sqlwatch_database_name nvarchar(max),
	--		@has_errors bit = 0


	--		set @has_errors = 0

	--		select @hostname = isnull(hostname, sql_instance), @sqlwatch_database_name = sqlwatch_database_name
	--		from [dbo].[sqlwatch_config_sql_instance]
	--		where sql_instance = @sql_instance

			

	--				set @sql = 'select * from openquery([' + @ls_server + '],''' + replace(@sql,'''','''''') + ''')'

	--				select @sql


	--				--exec sp_executesql @sql


SELECT CONVERT(INT,'am I used?')


END
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_repository_populate_tables_to_import]
AS

set nocount on;

truncate table dbo.sqlwatch_stage_repository_tables_to_import;

--list of tables to exclude from the import:
declare @exclude_tables table (
	table_name nvarchar(512)
)

--exclude tables that have mo meaning outside of the original SQL Instance:
insert into @exclude_tables
values	('sqlwatch_meta_action_queue'),
		('sqlwatch_meta_repository_import_queue'),
		('sqlwatch_meta_repository_import_status'),
		('sqlwatch_meta_repository_import_thread')

-- exclude tables that break the import that need fixing:
insert into @exclude_tables
values	('sqlwatch_logger_whoisactive'),
		('sqlwatch_logger_system_configuration_scd')


declare @include_tables table (
	table_name nvarchar(512)
)

insert into @include_tables
select name
from sys.tables
where name like 'sqlwatch_meta%' or	name like 'sqlwatch_logger%'


;with cte_base_tables (lvl, object_id, name, schema_Name) as (
	
	-- get base list of tables we will be importing
	select 1
		, object_id
		, t.name
		, [schema_Name] = s.name
	from sys.tables t 
	inner join sys.schemas s
		on t.schema_id = s.schema_id
	inner join @include_tables it
		on it.table_name = t.name
	where type_desc = 'USER_TABLE'
	and is_ms_shipped = 0
	and t.name not in (
		select table_name 
		from @exclude_tables
		)

	--now build dependencies so import tables in the right order:
	union all

	select 
		bt.lvl + 1, t.object_id, t.name, S.name as schema_Name
	from cte_base_tables bt
	inner join sys.tables t 
	on exists 
		 (	
			select null 
			from sys.foreign_keys fk
			where fk.parent_object_id = t.object_id
			and fk.referenced_object_id = bt.object_id 
			)
	inner join sys.schemas s 
		on t.schema_id = s.schema_id
		and t.object_id <> bt.object_id
		and bt.lvl < 20 -- this shoult correspond to the value in the SqlWatchImporter.exe
	inner join @include_tables it
		on it.table_name  = t.name
	where t.type_desc = 'USER_TABLE'
		and t.name not in (
			select table_name 
			from @exclude_tables
			)
		and t.is_ms_shipped = 0 
	)
, cte_dependency as (
	select 
		  table_name=d.schema_Name + '.' + d.name
		, dependency_level = MAX (d.lvl)
	from cte_base_tables d
	group by d.schema_Name, d.name
)
insert into dbo.sqlwatch_stage_repository_tables_to_import(
	[table_name],[dependency_level],[has_last_seen],[has_last_updated],
	[has_identity],[primary_key],[joins],[updatecolumns],[allcolumns] 
	)

select d.[table_name],d.[dependency_level],
	c.[has_last_seen],
	[has_last_updated],
	[has_identity],[primary_key],[joins],[updatecolumns],[allcolumns] 
from cte_dependency d

--check if the table contains date_last_seen column
outer apply (
	select has_last_seen = max(case when COLUMN_NAME = 'date_last_seen' then 1 else 0 end)
	from INFORMATION_SCHEMA.COLUMNS
	where TABLE_SCHEMA + '.' + TABLE_NAME = d.table_name
) c

-- check if the table has date_updated column
outer apply (
	select has_last_updated = max(case when COLUMN_NAME = 'date_updated' then 1 else 0 end)
	from INFORMATION_SCHEMA.COLUMNS
	where TABLE_SCHEMA + '.' + TABLE_NAME = d.table_name
) u

-- build concatenated string of primary keys
outer apply (
select primary_key = isnull(stuff ((
		select ',' + quotename(ccu.COLUMN_NAME)
			from INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
			inner join INFORMATION_SCHEMA.KEY_COLUMN_USAGE ccu
			on tc.CONSTRAINT_NAME = ccu.CONSTRAINT_NAME
		where tc.TABLE_NAME = parsename(d.TABLE_NAME,1)
		and tc.CONSTRAINT_TYPE = 'Primary Key'
		order by ccu.ORDINAL_POSITION
		for xml path('')),1,1,''),'')
	) pks

-- check if the table has identity
outer apply (
select has_identity = isnull(isnull(( 
		select 1
		from sys.identity_columns 
		where OBJECT_NAME(object_id) = parsename(d.TABLE_NAME,1)
		),0),'')
) hasidentity

-- build string containing all joins required for the merge operation
outer apply (
 select joins = isnull(stuff ((
		select ' and source.' + quotename(ccu.COLUMN_NAME) + ' = target.' + quotename(ccu.COLUMN_NAME)
			from INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
			inner join INFORMATION_SCHEMA.KEY_COLUMN_USAGE ccu
			on tc.CONSTRAINT_NAME = ccu.CONSTRAINT_NAME
		where tc.TABLE_NAME = parsename(d.TABLE_NAME,1)
		and tc.CONSTRAINT_TYPE = 'Primary Key'
		order by ccu.ORDINAL_POSITION
		for xml path('')),1,5,''),'')
) mergejoins

-- build update statememnt for the merge operation
outer apply (
select updatecolumns = isnull(stuff((
		select ',' + quotename(COLUMN_NAME) + '=source.' + quotename(COLUMN_NAME)
		from INFORMATION_SCHEMA.COLUMNS
		where TABLE_NAME = parsename(d.TABLE_NAME,1)

		--excluding primary keys
		and COLUMN_NAME not in (
				select ccu.COLUMN_NAME
				from INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
				inner join INFORMATION_SCHEMA.KEY_COLUMN_USAGE ccu
				on tc.CONSTRAINT_NAME = ccu.CONSTRAINT_NAME
				where tc.TABLE_NAME = parsename(d.TABLE_NAME,1)
				and tc.CONSTRAINT_TYPE = 'Primary Key'
		)
		--excluding computed columns 
		and COLUMN_NAME not in (
				select cc.name 
				from sys.computed_columns cc
				inner join sys.tables t
					on t.object_id = cc.object_id
				where t.name = parsename(d.TABLE_NAME,1)
		)

		--excluding identity columns (some may be outside of PK)
		and COLUMN_NAME not in (
				select ic.name
				from sys.identity_columns ic
				inner join sys.tables t
					on t.object_id = ic.object_id
				where t.name = parsename(d.TABLE_NAME,1)
		)
		order by ORDINAL_POSITION
		for xml path('')),1,1,''),'')
) updatecolumns

-- build string with all columns in the table
outer apply (
select allcolumns = isnull(stuff ((
		select ',' + quotename(COLUMN_NAME)
		from INFORMATION_SCHEMA.COLUMNS
		where TABLE_NAME = parsename(d.TABLE_NAME,1)
		--excluding computed columns 
		and COLUMN_NAME not in (
				select name 
				from sys.computed_columns
				where object_id = OBJECT_ID(d.TABLE_NAME)
		)
		order by ORDINAL_POSITION
		for xml path('')),1,1,''),'')
) allcolumns
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_repository_remote_table_dequeue]
	@sql_instance_out varchar(32) output,
	@object_name_out nvarchar(512) output,
	@load_type_out char(1) output
as
begin

	set xact_abort on;
	begin transaction

		declare @output table (
			sql_instance varchar(32),
			object_name nvarchar(512),
			load_type char(1)
		)

		;with cte_get_queue_item as (
			select top 1 * 
			from [dbo].[sqlwatch_meta_repository_import_queue] x with (readpast)
			where import_status = 'Ready' 
				--items without dependency on parent object:
				or (import_status is null and parent_object_name is null)
				--items with dependency on the parent object where the parent has been processed and dequeued:
				or (import_status is null and not exists (
						select * 
						from [dbo].[sqlwatch_meta_repository_import_queue]
						where object_name = x.parent_object_name
						)
					)
			order by [priority]
			) 
		update c
			set import_status = 'Running', [import_start_time] = SYSDATETIME()
		output deleted.sql_instance, deleted.object_name, deleted.load_type into @output
		from cte_get_queue_item c

	commit transaction

	select 
			@sql_instance_out = sql_instance
		,	@object_name_out = object_name
		,	@load_type_out = load_type
	from @output

return

end
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_repository_remote_table_enqueue]
	@force_full_load bit = 0
as

declare @batch_id uniqueidentifier = NEWID()

delete from [dbo].[sqlwatch_meta_repository_import_queue]
where not exists (select * from [dbo].[sqlwatch_meta_repository_import_thread])

if not exists (select * from [dbo].[sqlwatch_meta_repository_import_queue])
	begin
		;with cte_queue as (
			select sql_instance, 
				[object_name] = sqlwatch_database_name + '.' + t.TABLE_SCHEMA + '.' + t.TABLE_NAME ,
				[time_queued] = SYSUTCDATETIME(),
				[import_batch_id] = @batch_id,

				/* dependency object */
				[parent_object_name] = sqlwatch_database_name + '.' + t.TABLE_SCHEMA + '.' + case t.TABLE_NAME
					when 'sqlwatch_meta_server' then null
					when 'sqlwatch_meta_database' then 'sqlwatch_meta_server'
					when 'sqlwatch_meta_table' then 'sqlwatch_meta_database'
					when 'sqlwatch_meta_agent_job_step' then 'sqlwatch_meta_server'
					when 'sqlwatch_meta_master_file' then 'sqlwatch_meta_database'
					when 'sqlwatch_meta_index_missing' then 'sqlwatch_meta_table'
					when 'sqlwatch_meta_index' then 'sqlwatch_meta_table'
					when 'sqlwatch_meta_system_configuration_scd' then 'sqlwatch_logger_system_configuration'
					else 
						case 
							when t.TABLE_NAME like 'sqlwatch_meta%' then 'sqlwatch_meta_server'
							when t.TABLE_NAME = 'sqlwatch_logger_snapshot_header' then 'sqlwatch_meta_server'
							when t.TABLE_NAME like 'sqlwatch_logger%' then 'sqlwatch_logger_snapshot_header'
						else null end
					end ,

					[priority] = case when t.TABLE_NAME like 'sqlwatch_meta%' then 1 else 2 end
			from [dbo].[sqlwatch_config_sql_instance] s
				inner join INFORMATION_SCHEMA.TABLES t
				on TABLE_TYPE = 'BASE TABLE'
				and (
						TABLE_NAME LIKE 'sqlwatch_meta%'
					or	TABLE_NAME LIKE 'sqlwatch_logger%'
					)
				and TABLE_NAME NOT IN (
				/* do not pull any columns that have no meaning outside of the local instance
					maybe the logger_log would be useful to pull into central repo but will leave it out for now	*/
					  'sqlwatch_meta_action_queue','sqlwatch_logger_log'
					, 'sqlwatch_logger_check_action'
					, 'sqlwatch_app_log'
				)

				/* exclude central repo tables as they will be empty on the remotes anyway */
				and TABLE_NAME NOT LIKE ('sqlwatch_meta_repository_%')

				/* tables not yet used */
				and TABLE_NAME NOT IN (
					'sqlwatch_meta_sql_text'
				)

				/* linked servers do not support xml columns, lets skip these for now	*/
				and TABLE_NAME NOT IN (
					'THIS IS NOW SUPPORTED AND SHOULD WORK'
					--'sqlwatch_logger_xes_blockers', 'sqlwatch_logger_whoisactive '
					--select TABLE_NAME from INFORMATION_SCHEMA.COLUMNS
					--where DATA_TYPE = 'xml'
				)
			where [repo_collector_is_active] = 1
			and sql_instance <> @@SERVERNAME
			)

			insert into [dbo].[sqlwatch_meta_repository_import_queue] ([sql_instance], [object_name], [time_queued], [import_batch_id], [parent_object_name], [priority], [load_type])
			select s.[sql_instance], s.[object_name], s.[time_queued], s.[import_batch_id], s.[parent_object_name], s.[priority]
				,[load_type] = case 
					
					--when object_name like '%sqlwatch_logger_snapshot_header' then 'F' 
					when object_name like '%sqlwatch_logger%' and @force_full_load = 0 then 'D' 
					
					else 'F' end
					
			from cte_queue s
	end
else
	begin
		declare @message nvarchar(max) = 'Queue is not empty. In order to preserve data integrity, existing queue must complete first.'
		exec [dbo].[usp_sqlwatch_internal_log]
				@proc_id = @@PROCID,
				@process_stage = '19079A5C-F4C2-4268-9631-D47F419106E7',
				@process_message = @message,
				@process_message_type = 'WARNING'
	end


	merge [dbo].[sqlwatch_meta_repository_import_status] as target
	using [dbo].[sqlwatch_meta_repository_import_queue] as source
	on target.sql_instance = source.sql_instance
	and target.object_name = source.object_name

	when not matched then
		insert ([sql_instance], [object_name])
		values (source.[sql_instance], source.[object_name]);
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_repository_remote_table_import]
as

/*
-------------------------------------------------------------------------------------------------------------------
 Procedure:
	[usp_sqlwatch_repository_remote_table_import]

 Description:
	Central Repository procedure. Imports tables from remote SQLWATCH via linked server.

 Parameters
	N/A

 Author:
	Marcin Gminski

 Change Log:
	1.0		2020-xx-xx	- Marcin Gminski, Initial version
	1.1		2020-04-16	- Marcin Gminski, fixed error when running procedure manually not via agnt
-------------------------------------------------------------------------------------------------------------------
*/


set nocount on;
set xact_abort on; 

declare @sql_instance varchar(32),
		@object_name nvarchar(512),
		@load_type char(1),
		@sql nvarchar(max),
		@sql_remote nvarchar(max),
		@snapshot_time_start datetime2(0),
		@snapshot_time_end datetime2(0),
		@snapshot_type_id tinyint,
		@ls_server nvarchar(128),

		@join_keys nvarchar(max),
		@has_identity bit = 0,
		@table_name nvarchar(512),
		@table_schema nvarchar(128),
		@all_columns nvarchar(max),
		@pk_columns nvarchar(max),
		@nonpk_columns nvarchar(max),
		@has_errors bit = 0,
		@message nvarchar(max),
		@rmtq_timestart datetime2(7),
		@rmtq_timeend datetime2(7),
		@rowcount_imported bigint,
		@rowcount_loaded bigint,
		@database varchar(256),
		@object_name_t nvarchar(512),
		@thread_name nvarchar(max),
		@thread_spid nvarchar(max)

/* try obtain the agent job name that is running this particular thread */
select	@thread_name = j.name,
		@thread_spid = '(spid: ' + convert(varchar(10),p.spid) + ')'
		from master.dbo.sysprocesses p
		inner join msdb.dbo.sysjobs j
		on master.dbo.fn_varbintohexstr(convert(varbinary(16), job_id)) COLLATE Latin1_General_CI_AI =
		substring(replace(program_name, 'SQLAgent - TSQL JobStep (Job ', ''), 1, 34)
		where p.spid = @@SPID

if @thread_name is null
	begin
		set @thread_name = 'AD-HOC'
	end

if @thread_spid is null
	begin
		set @thread_spid = @@SPID
	end


set @message = 'Starting remote data import. Thread ' + @thread_name
exec [dbo].[usp_sqlwatch_internal_log]
		@proc_id = @@PROCID,
		@process_stage = 'A10C61BA-6EE9-40C9-BCD1-DBDCB9A232B7',
		@process_message = @message,
		@process_message_type = 'INFO'

merge [dbo].[sqlwatch_meta_repository_import_thread] as target
using (select thread_name = @thread_name) as source
on target.thread_name = source.thread_name
when matched then 
	delete
when not matched then 
	insert ( thread_name, thread_start_time )
	values (source.thread_name, SYSDATETIME());

while 1=1
	begin
		
		select @sql_instance = null, @object_name = null, @load_type = null, @sql = null, @has_errors = 0

		exec [dbo].[usp_sqlwatch_repository_remote_table_dequeue]
			@sql_instance_out = @sql_instance output,
			@object_name_out = @object_name output,
			@load_type_out = @load_type output

		select 
			@table_name = parsename(@object_name,1),
			@table_schema = parsename(@object_name,2),
			@database = parsename(@object_name,3)

		begin try
			begin transaction 
			exec [dbo].[usp_sqlwatch_repository_remote_table_import_worker] 
				@sql_instance = @sql_instance,
				@object_name = @object_name,
				@load_type = @load_type
			commit transaction 
		end try
			begin catch
				if @@TRANCOUNT > 0
				rollback transaction
				/*	In rare cases we may get Foreign key errors if the header table does not contain all data.
					Th snapshot_header table is the only delta loaded, logger table that has childs.
					Any other parent table is meta and always full loaded to avoid inconsistencies, however
					snapshot_header can be quite big so we load deltas. In case we have gaps,
					we will attempt to force a FULL load to try and fill any gaps	*/
				if ERROR_MESSAGE() like '%The INSERT statement conflicted with the FOREIGN KEY constraint%' 
					and ERROR_MESSAGE() like '%dbo.sqlwatch_logger_snapshot_header%' and @load_type = 'D'
					begin
						set @object_name_t = @database + '.dbo.sqlwatch_logger_snapshot_header'							
						set @message = 'FOREIGN KEY constraint failure, forcing full table load (sqlwatch_logger_snapshot_header)'

						exec [dbo].[usp_sqlwatch_internal_log]
								@proc_id = @@PROCID,
								@process_stage = 'FE99CFB8-7736-438B-8F21-9E04789B79A9',
								@process_message = @message,
								@process_message_type = 'WARNING'
							
						begin try

								/* rerun header table */
								exec [dbo].[usp_sqlwatch_repository_remote_table_import_worker] 
									@sql_instance = @sql_instance,
									@object_name = @object_name_t,
									@load_type = 'F'

								/* now re-run the child table */
								exec [dbo].[usp_sqlwatch_repository_remote_table_import_worker] 
									@sql_instance = @sql_instance,
									@object_name = @object_name,
									@load_type = @load_type

							GoTo Success
						end try
						begin catch
							if @@TRANCOUNT > 0
							rollback transaction
					
							set @has_errors = 1

							update dbo.[sqlwatch_meta_repository_import_status]
								set import_status = 'ERROR', [import_end_time] = SYSDATETIME(), [exec_proc] = @thread_name + ' ' + @thread_spid
							where sql_instance = @sql_instance
							and object_name = @object_name

							exec [dbo].[usp_sqlwatch_internal_log]
									@proc_id = @@PROCID,
									@process_stage = '4473A8F5-060C-4279-9B03-D81E5F0C5AE6',
									@process_message = 'Failed to force FULL table import.  Check errors in the worker thread.',
									@process_message_type = 'ERROR'

							GoTo NextItem
						end catch

						GoTo NextItem

						/*	remove any childs that we are not able to process because the parent has failed */
						delete from [dbo].[sqlwatch_meta_repository_import_queue]
						where sql_instance = @sql_instance
						and parent_object_name = @object_name
					end
				else
					begin
						if @@TRANCOUNT > 0
						rollback transaction

						set @has_errors = 1

						update dbo.[sqlwatch_meta_repository_import_status]
							set import_status = 'ERROR', [import_end_time] = SYSDATETIME(), [exec_proc] = @thread_name + ' ' + @thread_spid
						where sql_instance = @sql_instance
						and object_name = @object_name

						exec [dbo].[usp_sqlwatch_internal_log]
								@proc_id = @@PROCID,
								@process_stage = 'F649C8DB-8703-4AFB-AE65-C7E04E06AAD1',
								@process_message = 'Failed to import table. Check errors in the worker thread.',
								@process_message_type = 'ERROR'
					
						/*	remove any childs that we are not able to process because the parent has failed */
						delete from [dbo].[sqlwatch_meta_repository_import_queue]
						where sql_instance = @sql_instance
						and parent_object_name = @object_name

						GoTo NextItem
					end
			end catch
		
			Success:

			update dbo.[sqlwatch_meta_repository_import_status]
				set import_status = 'Success', [import_end_time] = SYSDATETIME(), [exec_proc] = @thread_name + ' ' + @thread_spid
			where sql_instance = @sql_instance
			and object_name = @object_name

			delete from [dbo].[sqlwatch_meta_repository_import_queue]
			where sql_instance = @sql_instance
			and object_name = @object_name

		NextItem:

		if @object_name is null
			begin
				Goto Finish
			end
	end


Finish:
set @message = 'Finished remote data import. Thread ' + @thread_name + ' ' + @thread_spid
exec [dbo].[usp_sqlwatch_internal_log]
		@proc_id = @@PROCID,
		@process_stage = '486B5F96-C8BA-441C-8D96-D25B0F2A0075',
		@process_message = @message,
		@process_message_type = 'INFO'

delete from [dbo].[sqlwatch_meta_repository_import_thread]
where thread_name = @thread_name

if @has_errors = 1
	begin
		declare @error_message nvarchar(max)
		set @error_message = 'Errors during execution (' + OBJECT_NAME(@@PROCID) + ')'
		--print all errors and terminate the batch which will also fail the agent job for the attention:
		raiserror ('%s',16,1,@error_message)
	end
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_repository_remote_table_import_worker]
	@sql_instance varchar(32),
	@object_name nvarchar(512),
	@load_type char(1)

as


declare @sql nvarchar(max),
		@sql_remote nvarchar(max),
		@snapshot_time_start datetime2(0),
		@snapshot_time_end datetime2(0),
		@snapshot_type_id tinyint,
		@ls_server nvarchar(128),

		@join_keys nvarchar(max),
		@has_identity bit = 0,
		@table_name nvarchar(512),
		@table_schema nvarchar(128),
		@all_columns nvarchar(max),
		@all_columns_from_source nvarchar(max),
		@all_columns_to_destination nvarchar(max),
		@pk_columns nvarchar(max),
		@nonpk_columns nvarchar(max),
		@has_errors bit = 0,
		@message nvarchar(max),
		@rmtq_timestart datetime2(7),
		@rmtq_timeend datetime2(7),
		@rowcount_imported bigint,
		@rowcount_loaded bigint,
		@update_columns nvarchar(max)


		select 
			@table_name = parsename(@object_name,1),
			@table_schema = parsename(@object_name,2)


				select @ls_server = linked_server_name
				from [dbo].[sqlwatch_config_sql_instance]
				where sql_instance = @sql_instance
				and linked_server_name is not null
				and repo_collector_is_active = 1

				/* get primary keys */
				select  @pk_columns = stuff ((
							select ',' + quotename(ccu.COLUMN_NAME)
								from INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
								inner join INFORMATION_SCHEMA.KEY_COLUMN_USAGE ccu
								on tc.CONSTRAINT_NAME = ccu.CONSTRAINT_NAME
							where tc.TABLE_NAME = @table_name 
							and tc.CONSTRAINT_TYPE = 'Primary Key'
							order by ccu.ORDINAL_POSITION
							for xml path('')),1,1,'')


				/* non primary key columns */
				select @nonpk_columns = stuff((
						select ',' + quotename(COLUMN_NAME)
						from INFORMATION_SCHEMA.COLUMNS
						where TABLE_NAME = @table_name

						and COLUMN_NAME not in (
								select ccu.COLUMN_NAME
								from INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
								inner join INFORMATION_SCHEMA.KEY_COLUMN_USAGE ccu
								on tc.CONSTRAINT_NAME = ccu.CONSTRAINT_NAME
								where tc.TABLE_NAME = @table_name
								and tc.CONSTRAINT_TYPE = 'Primary Key'
						)
						order by ORDINAL_POSITION
						for xml path('')),1,1,'')


				/* get columns */
				select @all_columns = stuff ((
						select ',' + quotename(COLUMN_NAME)
						from INFORMATION_SCHEMA.COLUMNS
						where TABLE_NAME = @table_name
						order by ORDINAL_POSITION
						for xml path('')),1,1,'')

				/* get columns, linked servers do not support xml data type so we need to convert to char and back to xml */
				select @all_columns_from_source = stuff ((
						select ',' + case when DATA_TYPE like '%xml%' then quotename(COLUMN_NAME) + ' = convert(nvarchar(max),' + quotename(COLUMN_NAME) + ')' else quotename(COLUMN_NAME) end
						from INFORMATION_SCHEMA.COLUMNS
						where TABLE_NAME = @table_name
						order by ORDINAL_POSITION
						for xml path('')),1,1,'')

				select @all_columns_to_destination = stuff ((
						select ',' + case when DATA_TYPE like '%xml%' then quotename(COLUMN_NAME) + ' = convert(xml,' + quotename(COLUMN_NAME) + ')' else quotename(COLUMN_NAME) end
						from INFORMATION_SCHEMA.COLUMNS
						where TABLE_NAME = @table_name
						order by ORDINAL_POSITION
						for xml path('')),1,1,'')


				/* update columns */
				select @update_columns = stuff((
						select ',' + quotename(COLUMN_NAME) + '=source.' + quotename(COLUMN_NAME)
						from INFORMATION_SCHEMA.COLUMNS
						where TABLE_NAME = @table_name

						and COLUMN_NAME not in (
								select ccu.COLUMN_NAME
								from INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
								inner join INFORMATION_SCHEMA.KEY_COLUMN_USAGE ccu
								on tc.CONSTRAINT_NAME = ccu.CONSTRAINT_NAME
								where tc.TABLE_NAME = @table_name
								and tc.CONSTRAINT_TYPE = 'Primary Key'
						)
						order by ORDINAL_POSITION
						for xml path('')),1,1,'')


				/* build joins */
				select @join_keys = stuff ((
							select ' and source.' + quotename(ccu.COLUMN_NAME) + ' = target.' + quotename(ccu.COLUMN_NAME)
								from INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
								inner join INFORMATION_SCHEMA.KEY_COLUMN_USAGE ccu
								on tc.CONSTRAINT_NAME = ccu.CONSTRAINT_NAME
							where tc.TABLE_NAME = @table_name AND 
							tc.CONSTRAINT_TYPE = 'Primary Key'
							order by ccu.ORDINAL_POSITION
							for xml path('')),1,5,'')

				/* check is table has identity */
				select @has_identity = isnull(( 
					select 1
					from sys.identity_columns
					where OBJECT_NAME(object_id) = @table_name
					),0)







			------------------------------------------------------------------------------------------------------------
			-- FULL LOAD
			------------------------------------------------------------------------------------------------------------
			if @load_type = 'F'
				begin
					set @sql = 'select ' + @all_columns_from_source + ' from ' + @object_name
					set @sql = '
select ' + @all_columns_to_destination + ' 
into #t
from openquery([' + @ls_server + '],''' + replace(@sql,'''','''''') + ''')
set @rowcount_imported_out = @@ROWCOUNT

alter table #t add primary key (' + @pk_columns + ');

' + case when @has_identity = 1 then 'set identity_insert ' + quotename(@table_name) + ' on' else '' end + '
;merge ' + quotename(@table_name) + ' as target
using #t as source 
on ( ' + @join_keys + ' )

when matched
	then update set ' 
	+ @update_columns + '
		
when not matched 
	then insert ( ' + @all_columns + ')
	values ( source.' + replace(@all_columns,',',',source.') + ')
;
set @rowcount_loaded_out = @@ROWCOUNT
' + case when @has_identity = 1 then 'set identity_insert ' + quotename(@table_name) + ' off' else '' end + '
;

'				


				end




			------------------------------------------------------------------------------------------------------------
			-- DELTA LOAD
			------------------------------------------------------------------------------------------------------------
			if @load_type = 'D'
				begin
					select @snapshot_type_id = snapshot_type_id
					from vw_sqlwatch_internal_table_snapshot
					where table_name = parsename(@object_name,1)

					/*	get current max snapshot_time to calcualte delta from remote	*/
					set @sql = 'select @snapshot_time_start_out = max(snapshot_time) from ' + @object_name + ' where sql_instance = ''' + @sql_instance + ''''
					
					begin try
						exec sp_executesql @sql , N'@snapshot_time_start_out datetime2(0) OUTPUT', @snapshot_time_start_out = @snapshot_time_start output;
					end try
					begin catch
						exec [dbo].[usp_sqlwatch_internal_log]
							@proc_id = @@PROCID,
							@process_stage = '985164F4-C2E8-49F9-A582-E4CDF5385406',
							@process_message = @sql,
							@process_message_type = 'ERROR'
					end catch
					/*	get current max snapshot_time from the header so we are not trying to insert any data that is not yet in the header */
					set @sql = 'select @snapshot_time_end_out = max(snapshot_time) 
from dbo.sqlwatch_logger_snapshot_header
where sql_instance = ''' + @sql_instance + '''
and snapshot_type_id = ' + convert(varchar(10),@snapshot_type_id)

					begin try
						exec sp_executesql @sql, N'@snapshot_time_end_out datetime2(0) OUTPUT', @snapshot_time_end_out = @snapshot_time_end output;
					end try
					begin catch
						exec [dbo].[usp_sqlwatch_internal_log]
							@proc_id = @@PROCID,
							@process_stage = 'CCEC28A0-4F4A-4BA2-B17A-CF59434F77ED',
							@process_message = @sql,
							@process_message_type = 'ERROR'
					end catch
				
					/*	build the remote command limited to dates from the above calcualations	*/
					set @sql = 'select ' + @all_columns_from_source + ' from ' + @object_name + '
where sql_instance = ''' + @sql_instance + ''' 
and snapshot_time > ''' + isnull( convert(varchar(23),@snapshot_time_start,121),'1970-01-01') + '''
' 
/* we want to pull all new headers , all the other logger tables we are pulling new records but limited to the most recent header */
+ case when @table_name <> 'sqlwatch_logger_snapshot_header' then 'and snapshot_time <= ''' + isnull( convert(varchar(23),@snapshot_time_end,121), '1970-01-01') + '''' else '' end

					set @sql = '
' + case when @has_identity = 1 then 'set identity_insert ' + quotename(@table_name) + ' on' else '' end + '
insert into '+ quotename(@table_schema) + '.' + quotename(@table_name) + ' (' + @all_columns + ')
select ' + @all_columns_to_destination + ' from openquery([' + @ls_server + '],''' + replace(@sql,'''','''''') + ''')
set @rowcount_loaded_out = @@ROWCOUNT
' + case when @has_identity = 1 then 'set identity_insert ' + quotename(@table_name) + ' off' else '' end + '
					'
				end

		select @rowcount_imported = null, @rowcount_loaded = null

		if @sql is null
			begin
				return
			end

			set @rmtq_timestart = sysutcdatetime()
			begin try
				exec sp_executesql @sql, N'@rowcount_imported_out bigint OUTPUT, @rowcount_loaded_out bigint OUTPUT', @rowcount_imported_out = @rowcount_imported output, @rowcount_loaded_out = @rowcount_loaded output;
			end try
			begin catch
				exec [dbo].[usp_sqlwatch_internal_log]
					@proc_id = @@PROCID,
					@process_stage = '9B115374-36F7-484F-810F-8B9EB2307342',
					@process_message = @sql,
					@process_message_type = 'ERROR'
			end catch
			set @rmtq_timeend = sysutcdatetime()
					
			set @message = 'Retrieving data from remote instance (' + @sql_instance + '). '
			set @message = @message + case when @rowcount_imported is not null then 'Imported ' + convert(varchar(10),@rowcount_imported) + ' rows from remote table (' + @object_name + '). ' else '' end
			set @message = @message + case when @rowcount_loaded is not null then 'Loaded ' + convert(varchar(10),@rowcount_loaded) + ' rows into local table (' + @table_name + '). ' else '' end

			set @message = @message + 'Time Start: ' + convert(varchar(23),@rmtq_timestart,121) + ', Time End: ' + convert(varchar(23),@rmtq_timeend,121) + ', Time Taken (ms): ' + convert(varchar(10),datediff(ms,@rmtq_timestart,@rmtq_timeend),121)

			exec [dbo].[usp_sqlwatch_internal_log]
				@proc_id = @@PROCID,
				@process_stage = '11592B38-3F3F-4E91-87ED-6C7DD0CDC483',
				@process_message = @message,
				@process_message_type = 'INFO'
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[usp_sqlwatch_trend_perf_os_performance_counters]
	@interval_minutes smallint = 60,
	@valid_days smallint = 720
as

set xact_abort on;
set nocount on;

declare		@snapshot_time datetime2(0),
			@first_snapshot_time datetime2(0), 
			@last_snapshot_time datetime2(0),
			@snapshot_time_utc_offset int

  select 
	  @first_snapshot_time =min(snapshot_time)
	, @last_snapshot_time=max(snapshot_time)
	, @snapshot_time_utc_offset = max(snapshot_time_utc_offset)
  from [dbo].[sqlwatch_logger_snapshot_header] h
  where datepart(hour,h.snapshot_time) = datepart(hour,dateadd(hour,-1,getutcdate()))
  and datepart(day,h.snapshot_time) = datepart(day,dateadd(hour,-1,getutcdate()))
  and datepart(month,h.snapshot_time) = datepart(month,dateadd(hour,-1,getutcdate()))
  and datepart(year,h.snapshot_time) = datepart(year,dateadd(hour,-1,getutcdate()))

  insert into [dbo].[sqlwatch_trend_perf_os_performance_counters] (
		performance_counter_id
		, instance_name
		, sql_instance
		, cntr_value_calculated_avg
		, cntr_value_calculated_min
		, cntr_value_calculated_max
		, cntr_value_calculated_sum
		, interval_minutes
		, snapshot_time
		, snapshot_time_offset
		, valid_until
		)
  select pc.[performance_counter_id]
      ,pc.[instance_name]
      ,pc.[sql_instance]
      ,[cntr_value_calculated_avg] = avg(pc.[cntr_value_calculated])
	  ,[cntr_value_calculated_min] = min(pc.[cntr_value_calculated])
	  ,[cntr_value_calculated_max] = max(pc.[cntr_value_calculated])
	  ,[cntr_value_calculated_sum] = sum(pc.[cntr_value_calculated])
	  ,[interval_minutes] = @interval_minutes --datediff(minute,@first_snapshot_time,@last_snapshot_time)
	  , snapshot_time = dateadd(minute, datediff(minute, 0, h.snapshot_time ) / @interval_minutes * @interval_minutes, 0)
	  ,[snapshot_time_offset] = TODATETIMEOFFSET ( dateadd(minute, datediff(minute, 0, h.snapshot_time ) / @interval_minutes * @interval_minutes, 0) , h.snapshot_time_utc_offset )  
		/*
	  , snapshot_time = dateadd(hour, datediff(hour, 0, h.snapshot_time), 0)
	  ,[snapshot_time_offset] = TODATETIMEOFFSET ( dateadd(hour, datediff(hour, 0, h.snapshot_time), 0) , h.snapshot_time_utc_offset )  
		*/
	   , valid_until = dateadd(day,@valid_days,getutcdate())
  from [dbo].[sqlwatch_logger_perf_os_performance_counters] pc
  
  inner join [dbo].[sqlwatch_logger_snapshot_header] h
	on pc.sql_instance = h.sql_instance
	and pc.snapshot_time = h.snapshot_time
	and pc.snapshot_type_id = pc.snapshot_type_id

  inner join [dbo].[sqlwatch_meta_performance_counter] mpc
	on mpc.performance_counter_id = pc.performance_counter_id
	and mpc.sql_instance = pc.sql_instance

  left join [dbo].[sqlwatch_trend_perf_os_performance_counters] t
	on t.snapshot_time = dateadd(minute, datediff(minute, 0, h.snapshot_time ) / @interval_minutes * @interval_minutes, 0)
	and t.[instance_name] = pc.[instance_name]
	and t.[sql_instance] = pc.[sql_instance]
	and t.[interval_minutes] = @interval_minutes
	and t.[performance_counter_id] = pc.[performance_counter_id]

  where mpc.cntr_type <> 1073939712  --exclude base counters
  and h.snapshot_time >= @first_snapshot_time
  and h.snapshot_time <= @last_snapshot_time
  and pc.sql_instance = dbo.ufn_sqlwatch_get_servername()
  and (	
			t.snapshot_time is null
		and	t.instance_name is null
		and t.sql_instance is null
		and t.interval_minutes is null
		and t.performance_counter_id is null
		)

  group by  pc.[performance_counter_id]
      ,pc.[instance_name]
      ,pc.[sql_instance]  
	  , dateadd(minute, datediff(minute, 0, h.snapshot_time ) / @interval_minutes * @interval_minutes, 0)
	  , TODATETIMEOFFSET ( dateadd(minute, datediff(minute, 0, h.snapshot_time ) / @interval_minutes * @interval_minutes, 0) , h.snapshot_time_utc_offset )
GO
EXEC [SQLWATCH].sys.sp_addextendedproperty @name=N'SQLWATCH Version', @value=N'4.11.0.576' 
GO
EXEC [SQLWATCH].sys.sp_addextendedproperty @name=N'SQLWATCH License', @value=N'https://github.com/marcingminski/sqlwatch

---

Some parts of the code come from MSSQL Tiger Team (https://blogs.msdn.microsoft.com/sql_server_team) 
https://github.com/microsoft/tigertoolbox avaiable under MIT license.
Microsoft SQL Server Sample Code
Copyright (c) Microsoft Corporation
All rights reserved.
MIT License.

---

MIT License

Copyright (c) 2018 Marcin Gminski https://marcin.gminski.net

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.' 
GO
EXEC [SQLWATCH].sys.sp_addextendedproperty @name=N'SQLWATCH Documentation', @value=N'https://sqlwtach.io
https://docs.sqlwtach.io' 
GO
USE [master]
GO
ALTER DATABASE [SQLWATCH] SET  READ_WRITE 
GO
