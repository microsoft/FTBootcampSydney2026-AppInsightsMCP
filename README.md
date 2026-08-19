## Dataverse App Insights + Dataverse MCP demo tools

- agents-and-mcp-example contains the three agents used in the demo
    - DV Performance Analyst takes a look at Dataverse perf (UCI+API), retrieving the user's IDs for query via DV MCP
    - Plugin Performance Agent looks at plugin performance and compares to source code
    - Plugin Reliability Agent looks at plugin reliability over last x days and makes recommendations based on crash data and source code
- dataverse-bulkload contains a utility to upload lots of data to Dataverse for demo purposes. It is set to the shape of the entities stored in the solution file attached.
- plugins-source contians the source code for the two plugins used in the demo. They are also compiled into the solution package.
- solution contains an unmanaged solution using the entities shown in the demo

If you've got any questions or queries about what's stored in this repo, please feel free to drop me a line - remymoreland (at) microsoft dot com