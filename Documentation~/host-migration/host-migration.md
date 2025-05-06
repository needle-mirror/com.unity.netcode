# Host migration

> [!NOTE]
> Host migration is an experimental feature so the API and implementation can change in the future. By default it's not exposed, enable it by adding the `ENABLE_HOST_MIGRATION` define in the __Scripting Define Symbols__ in the __Player__ tab of the project settings.

Use host migration in your project to allow a client hosted networking experience to continue after the loss of the host.

You can use host migration to manage a variety of voluntary and involuntary interruptions, including network disconnections, power failures, or the host exiting the application.

| **Topic**                       | **Description**                  |
| :------------------------------ | :------------------------------- |
| **[Introduction to host migration](host-migration-intro.md)** | Understand the basics of host migration in Netcode for Entities and whether it might be suitable for your project. |
| **[Host migration API and components](host-migration-api.md)** | Understand the host migration API, components, and component options. |
| **[Add host migration to your project](add-host-migration.md)**  | Understand the requirements, systems, and integrations involved in adding host migration to your project. |
| **[Limitations and known issues](host-migration-limitations.md)** | Understand the limitations and known issues with host migration to implement it most effectively in your project. |
