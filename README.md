# Courier Dispatch

A dispatch system for a courier business in Arlington, VA:

- **Dispatch board** (Blazor Server): live queue, drivers, routes, drag-and-drop assignment, presence, conflict-safe assignment
- **Live map**: driver pins, today's trails, planned routes, exceptions
- **Driver app** (.NET MAUI Blazor Hybrid, iPhone + Android): background GPS, stop actions, photo and signature proof
- **Job intake**: new job form with address autocomplete, CSV import with remembered layouts, Order API, Shopify webhooks
- **Route optimization** (GraphHopper), **live ETAs** (Mapbox Directions), **customer texts** and **tracking pages**
- **People, Clients, and Settings** pages, including a closed-day (holiday) calendar

The file index.html in the repository root is a separate, self-contained demo with sample data (published with GitHub Pages).

## What is in this repository

This repository contains the files written for this project. The standard files that the .NET templates generate (login pages, project files, MAUI platform setup) are created on your machine with the commands below and copied in without overwriting anything.

    src/Dispatch.Shared   Enums and API contracts shared by the server and the phone app
    src/Dispatch.Web      Blazor Server dashboard, driver API, SignalR hub, background services
    src/Dispatch.Driver   .NET MAUI Blazor Hybrid driver app

## First-time setup

Requirements: .NET SDK (the version you use for the templates), the MAUI workload (dotnet workload install maui), PostgreSQL, and the EF Core tools (dotnet tool install --global dotnet-ef).

### 1. Generate the template files and copy in the missing ones

From the repository root (macOS/Linux):

    mkdir -p ../template-temp
    dotnet new blazor -n Dispatch.Web -int Server -au Individual -o ../template-temp/Dispatch.Web
    dotnet new maui-blazor -n Dispatch.Driver -o ../template-temp/Dispatch.Driver
    rsync -a --ignore-existing ../template-temp/Dispatch.Web/ src/Dispatch.Web/
    rsync -a --ignore-existing ../template-temp/Dispatch.Driver/ src/Dispatch.Driver/
    dotnet new classlib -n Dispatch.Shared -o src/Dispatch.Shared
    rm src/Dispatch.Shared/Class1.cs

On Windows, replace the two rsync lines with:

    robocopy ..\template-temp\Dispatch.Web src\Dispatch.Web /E /XC /XN /XO
    robocopy ..\template-temp\Dispatch.Driver src\Dispatch.Driver /E /XC /XN /XO

Then remove template pieces this project replaces:

    rm -rf src/Dispatch.Web/Data/Migrations
    rm src/Dispatch.Driver/Components/Pages/Counter.razor src/Dispatch.Driver/Components/Pages/Weather.razor

### 2. Solution, references, and packages

    dotnet new sln -n Dispatch
    dotnet sln add src/Dispatch.Web src/Dispatch.Shared src/Dispatch.Driver
    dotnet add src/Dispatch.Web reference src/Dispatch.Shared
    dotnet add src/Dispatch.Driver reference src/Dispatch.Shared

    cd src/Dispatch.Web
    dotnet remove package Microsoft.EntityFrameworkCore.Sqlite
    dotnet add package Npgsql.EntityFrameworkCore.PostgreSQL
    dotnet add package CsvHelper
    dotnet add package Azure.Storage.Blobs
    dotnet add package Azure.Extensions.AspNetCore.DataProtection.Blobs
    dotnet add package Azure.Communication.Email
    dotnet add package Azure.Communication.Sms
    cd ../Dispatch.Driver
    dotnet add package Microsoft.AspNetCore.SignalR.Client
    dotnet add package Microsoft.Extensions.Http
    cd ../..

### 3. Small edits to template files

**src/Dispatch.Web/Program.cs**

1. Add using Dispatch.Web.Hosting; at the top.
2. Delete the template's connection string line and its builder.Services.AddDbContext<ApplicationDbContext>(...) line (this project uses PostgreSQL through a context factory).
3. Delete the line that registers IdentityNoOpEmailSender (real email is registered by this project).
4. In the AddIdentityCore<ApplicationUser>(...) chain, add .AddRoles<IdentityRole>().
5. Just before var app = builder.Build(); add: builder.AddDispatch();
6. Just after var app = builder.Build(); add: app.UseDispatchMiddleware();
7. Just before app.Run(); add: await app.MapDispatchAsync();

**src/Dispatch.Web/Components/App.razor**, inside head:

    <link rel="stylesheet" href="https://unpkg.com/leaflet@1.9.4/dist/leaflet.css" />
    <link rel="stylesheet" href="css/dispatch.css" />
    <link rel="stylesheet" href="css/admin.css" />

and just above the blazor.web.js script tag:

    <script src="https://unpkg.com/leaflet@1.9.4/dist/leaflet.js"></script>

**src/Dispatch.Driver/Dispatch.Driver.csproj**: set the targets to phones only and your app id:

    <TargetFrameworks>net10.0-android;net10.0-ios</TargetFrameworks>
    <ApplicationId>com.yourco.driver</ApplicationId>

(keep whatever .NET version number the template generated).

**src/Dispatch.Driver/Platforms/Android/AndroidManifest.xml**, inside manifest:

    <uses-permission android:name="android.permission.INTERNET" />
    <uses-permission android:name="android.permission.ACCESS_FINE_LOCATION" />
    <uses-permission android:name="android.permission.ACCESS_COARSE_LOCATION" />
    <uses-permission android:name="android.permission.ACCESS_BACKGROUND_LOCATION" />
    <uses-permission android:name="android.permission.FOREGROUND_SERVICE" />
    <uses-permission android:name="android.permission.FOREGROUND_SERVICE_LOCATION" />
    <uses-permission android:name="android.permission.POST_NOTIFICATIONS" />
    <uses-permission android:name="android.permission.CAMERA" />
    <uses-permission android:name="android.permission.VIBRATE" />
    <queries>
      <intent>
        <action android:name="android.media.action.IMAGE_CAPTURE" />
      </intent>
    </queries>

**src/Dispatch.Driver/Platforms/iOS/Info.plist**, inside the top-level dict:

    <key>UIBackgroundModes</key>
    <array><string>location</string></array>
    <key>NSLocationWhenInUseUsageDescription</key>
    <string>Dispatch sees your location while you're on shift so they can assign nearby jobs.</string>
    <key>NSLocationAlwaysAndWhenInUseUsageDescription</key>
    <string>Dispatch sees your location while you're on shift, even when your phone is locked.</string>
    <key>NSCameraUsageDescription</key>
    <string>Take photos as proof of delivery.</string>

### 4. Secrets (never commit these)

    cd src/Dispatch.Web
    dotnet user-secrets init
    dotnet user-secrets set "ConnectionStrings:Dispatch" "Host=localhost;Database=dispatch;Username=dispatch;Password=..."
    dotnet user-secrets set "Mapbox:Token" "pk...."
    dotnet user-secrets set "Routing:GraphHopperKey" "..."
    dotnet user-secrets set "Seed:DispatcherEmail" "you@yourco.com"
    dotnet user-secrets set "Seed:DispatcherPassword" "..."
    dotnet user-secrets set "Seed:DriverEmail" "driver@yourco.com"
    dotnet user-secrets set "Seed:DriverPassword" "..."

Non-secret defaults (service times, delivery days, branding) are in src/Dispatch.Web/appsettings.Dispatch.json.

### 5. Database and first run

    cd src/Dispatch.Web
    dotnet ef migrations add Initial
    dotnet ef database update
    dotnet run

Sign in at /Account/Login with the seeded dispatcher, then open /dispatch.

In development, emails and customer texts are written to the console instead of being sent, and a GPS simulator moves on-shift sample drivers so the map can be tested without phones.

### 6. Driver app

Set the server address in src/Dispatch.Driver/Services/AppConfig.cs (use a dev tunnel URL for development, since phones cannot reach localhost), then run the Dispatch.Driver project on an Android phone or a registered iPhone.

## Production

See the deployment notes in the project conversation: Azure App Service (one instance, WebSockets and session affinity on), Azure Database for PostgreSQL, a Storage account (proof photos and sign-in keys), and Azure Communication Services (email and SMS). Configure secrets as App Service settings, for example ConnectionStrings__Dispatch, Mapbox__Token, Routing__GraphHopperKey, Storage__ConnectionString, Email__AcsConnectionString, Email__From, Sms__AcsConnectionString, Sms__From, App__PublicBaseUrl.
