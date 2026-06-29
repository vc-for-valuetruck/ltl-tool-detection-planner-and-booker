-- Template Seed Data
-- Replace with your app-specific demo data.

IF NOT EXISTS (SELECT name FROM sys.databases WHERE name = 'MyApp')
BEGIN
  CREATE DATABASE MyApp;
END
GO

USE MyApp;
GO

-- Add your tables and seed data here
