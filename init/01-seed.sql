-- Template Seed Data
-- Replace with your app-specific demo data.

IF NOT EXISTS (SELECT name FROM sys.databases WHERE name = 'LtlTool')
BEGIN
  CREATE DATABASE LtlTool;
END
GO

USE LtlTool;
GO

-- Add your tables and seed data here
