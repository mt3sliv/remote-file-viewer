# Phase 2 implementation status

The package-owned Windows x64 Office preview is implemented. `RemoteFrame.Office.Windows` aggregates `RemoteFrame.AspNetCore` and `RemoteFrame.Office.Runtime.win-x64`. Native binaries are staged and packaged locally under `artifacts/`; no package has been published externally.

Implemented: full bundled LibreOffice Writer/Calc/Impress binaries, private Windows desktops, isolated profiles and document working copies, GDI/PrintWindow PNG capture, native input forwarding, Save propagation, content-hash conflict checks and job-object process cleanup. The anonymous loopback demo consumes the aggregate NuGet package and automatically registers all sample files.

Verified here: Writer/Calc private-desktop capture, typing and save persistence using copied samples; native package inventory/integrity rejection cases; transitive asset delivery to build/publish outputs. Additional end-to-end demo checks are recorded in the chat.

Still required before a general release: Linux/ARM64 implementations and native payloads, clean-machine dependency verification, IIS/service acceptance, a broader document/input/menu compatibility suite, conflict recovery, runtime resource quotas beyond session counts/timeouts, and distribution license/source-delivery review. The current Windows distribution is staged from the locally available upstream LibreOffice build, pinned by its build revision; the new source-build pipeline is implemented but has not compiled an engine on this machine because Cygwin build prerequisites are absent.

Source-build instructions: [pinned source pipeline](packaging/libreoffice/README.md). It is intentionally established before any VBA compatibility patches. The current demo package is unchanged until a source build is validated.
