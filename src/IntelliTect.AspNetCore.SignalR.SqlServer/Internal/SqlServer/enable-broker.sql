
DECLARE @is_broker_enabled bit;

SELECT @is_broker_enabled=is_broker_enabled FROM sys.databases WHERE database_id=db_id()

-- If there's no row, probably means we're on Azure SQL, which doesn't support service broker.
IF @@ROWCOUNT = 0 return;

IF @is_broker_enabled <> 1 BEGIN
	ALTER DATABASE CURRENT SET ENABLE_BROKER WITH NO_WAIT;
END