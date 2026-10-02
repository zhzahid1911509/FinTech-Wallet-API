## FinTech Digital Wallet API

# Overview

A secure, scalable RESTful API for a digital wallet system, built with ASP.NET Core 8 and Entity Framework Core. This system handles user authentication and ACID-compliant financial transactions using Optimistic Concurrency Control.

# Architecture

The project follows a Layered Architecture to enforce the Separation of Concerns and Dependency Inversion:

-- Presentation Layer: ASP.NET Core Web API Controllers

-- Application Layer: Business Logic and Interfaces (Services)

-- Infrastructure Layer: EF Core DbContext, Repositories, and SQL Server

# Key Features

JWT-based Authentication & Authorization.

Secure peer-to-peer funds transfer using explicit database transactions.

Concurrency conflict resolution using Read Committed Snapshot Isolation (RCSI) and RowVersion.

Global Exception Handling.

# Tech Stack

.NET 8 / C#

Entity Framework Core

SQL Server

Swagger / OpenAPI

# Local Setup Instructions

Clone the repository.

Update the DefaultConnection string in appsettings.json to point to your local SQL Server instance.

Open your terminal in the project directory and run the Entity Framework migrations:
dotnet ef database update

Run the application:
dotnet run

Navigate to https://localhost:<port>/swagger to test the endpoints.

# FinTech Digital Wallet API

## Overview

A secure, scalable RESTful API for a digital wallet system, built with ASP.NET Core 8 and Entity Framework Core. This system handles user authentication and ACID-compliant financial transactions using Optimistic Concurrency Control.

## Architecture

The project follows a Layered Architecture to enforce the Separation of Concerns and Dependency Inversion:

- Presentation Layer: ASP.NET Core Web API Controllers

- Application Layer: Business Logic and Interfaces (Services)

- Infrastructure Layer: EF Core DbContext, Repositories, and SQL Server

## Key Features

- JWT-based Authentication & Authorization.

- Secure peer-to-peer funds transfer using explicit database transactions.

- Concurrency conflict resolution using Read Committed Snapshot Isolation (RCSI) and RowVersion.

- Global Exception Handling.

## Tech Stack

- .NET 8 / C#

- Entity Framework Core

- SQL Server

- Swagger / OpenAPI

## Local Setup Instructions

1. Clone the repository.

2. Update the DefaultConnection string in appsettings.json to point to your local SQL Server instance.

3. Open your terminal in the project directory and run the Entity Framework migrations:
   `dotnet ef database update`

4. Run the application:
   `dotnet run`

5. Navigate to https://localhost:<port>/swagger to test the endpoints.
