# How to create the nuget package for Blueprint.Api.Client
1. cd ../Blueprint.Api
2. dotnet build
3. swagger tofile --output ../Blueprint.Api.Client/swagger.json bin/Debug/net10.0/Blueprint.Api.dll v1
4. cd ../Blueprint.Api.Client
5. npm install
6. ./node_modules/.bin/nswag run /runtime:Net60
7. dotnet pack -c Release /p:version=x.y.z


# To install swagger on Windows managed machine
1. Create folder C:\SEI\Tools\.dotnet\tools and copy contents from C:\Users\<usrname>\.dotnet\tools
2. Delete C:\Users\<usrname>\.dotnet\tools and then create a symlink from C:\Users\<usrname>\.dotnet\tools to C:\SEI\Tools\.dotnet\tools
3. dotnet tool install --global Swashbuckle.AspNetCore.Cli --version 10.0.1

   ** (note:  the version MUST match the `Swashbuckle.AspNetCore` version in Directory.Packages.props)
