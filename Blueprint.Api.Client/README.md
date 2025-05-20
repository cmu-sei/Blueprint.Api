# How to create the nuget package for Blueprint.Api.Client
1. cd ../Blueprint.Api
2. swagger tofile --output ../Blueprint.Api.Client/swagger.json bin/Debug/net8.0/Blueprint.Api.dll v1
3. cd ../Blueprint.Api.Client
4. npm install
5. ./node_modules/.bin/nswag run /runtime:Net60
6. dotnet pack -c Release /p:version=0.1.2


# To install swagger on Windows managed machine
1. Create folder C:\SEI\Tools\.dotnet\tools and copy contents from C:\Users\<usrname>\.dotnet\tools
2. Delete C:\Users\<usrname>\.dotnet\tools and then create a symlink from C:\Users\<usrname>\.dotnet\tools to C:\SEI\Tools\.dotnet\tools
3. dotnet tool install --global Swashbuckle.AspNetCore.Cli --version 6.2.3
   ** (note:  the version MUST match what is in Blueprint.Api.csproj)
