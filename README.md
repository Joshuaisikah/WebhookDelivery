# WebhookDelivery

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                      API Layer                             │
│  (Controllers/Endpoints, DTOs, Validators, Middleware)    │
│  Composition root: wires up Infrastructure's DI here      │
│         ↓                                                 │
│                   Application Layer                        │
│  (Commands, Queries, Handlers, Application Services)      │
│         ↓                                                 │
│                    Domain Layer                            │
│  (Entities, Value Objects, Domain Events, Interfaces)     │
│         ↑                                                 │
│               Infrastructure Layer                         │
│  (Repositories, DbContext, External Services)             │
└─────────────────────────────────────────────────────────────┘
```

## Layers
- **Api**
- **Infrastructure**
- **Tests**

## Dependency Flow
- API → Domain (Services live in Domain; no separate Application project)
- API → Infrastructure (composition root only — Program.cs wiring, no business logic calls this directly)
- Infrastructure → Domain (implements its interfaces)
- Tests → All layers

## API Documentation (Scalar)

Scalar is pre-configured. After `dotnet run`:

- OpenAPI document → `/openapi/v1.json`
- **Scalar UI**     → `/scalar`   ← this is also the launch URL

When you run the API the console shows the listening addresses.
Just append `/scalar` (or click the link if your terminal supports it).

## Setup

```bash
dotnet restore
dotnet build
cd WebhookDelivery.Api
dotnet run
# → browser / terminal link will open at /scalar
```

## Tests

```bash
dotnet test
```
