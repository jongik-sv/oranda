# Third-party notices

Oranda is licensed under the MIT License (see [LICENSE](LICENSE)). The Oranda installers and portable archives also contain the third-party components listed below. Each component remains under its own license.

How this list was made (2026-10-25): every runtime package in `Directory.Packages.props` plus the packages they pull into the published app (`dotnet list src/Oranda.App package --include-transitive`), with the license read from each package's `.nuspec` `license` element and the license files inside the package in the local NuGet cache. Test-only packages (`Avalonia.Headless`, `coverlet.collector`, `Microsoft.NET.Test.Sdk`, `xunit`, `xunit.runner.visualstudio`, `Testcontainers.Oracle`) are not shipped and are not listed. "Unverified" means the license could not be confirmed from the package itself.

## Oracle Data Provider for .NET (important)

| Item | Value |
|---|---|
| Package | `Oracle.ManagedDataAccess.Core` 23.26.301 |
| Copyright | Copyright © 2026, Oracle and/or its affiliates |
| License | Oracle Free Distribution, Hosting, and Use Terms and Conditions, Version 1.0, last updated 28 June 2022 (`license type="file"` → `LICENSE.txt` in the package). This is **not** an open source license. |
| Full text | [Appendix A](#appendix-a-oracle-free-distribution-hosting-and-use-terms-and-conditions), and Oracle's separately licensed technology notices in [Appendix B](#appendix-b-oracle-data-provider-for-net-separately-licensed-technology-notices) |

Redistribution conditions that apply to Oranda (summary of the license; the full text in Appendix A governs):

- Only the **unmodified** Oracle program may be redistributed. Oranda ships the DLL as published by Oracle and does not recompile it (`PublishReadyToRunExclude` in `src/Oranda.App/Oranda.App.csproj` keeps Oracle's own Authenticode signature).
- A **copy of the Oracle license must be included with every distribution**. The release workflow copies this file into the installed app folder (`licenses/`).
- No additional fee may be charged for the distribution or use of the Oracle program. Oranda is free.
- Oracle's and its licensors' proprietary notices must not be removed.
- U.S. and other applicable export control and sanctions laws apply.
- Reverse engineering, disassembly or decompilation of the Oracle program is not permitted (except as allowed by law).

## Packages shipped with Oranda

| Package | Version | License | Copyright / authors | Project |
|---|---|---|---|---|
| Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent, Avalonia.Controls.ColorPicker | 12.1.4 | MIT | Copyright 2013-2026 © The AvaloniaUI Project | https://github.com/AvaloniaUI/Avalonia |
| Avalonia.Skia, Avalonia.HarfBuzz, Avalonia.Win32, Avalonia.Native, Avalonia.X11, Avalonia.FreeDesktop, Avalonia.FreeDesktop.AtSpi, Avalonia.Remote.Protocol (transitive) | 12.1.4 | MIT | Copyright 2013-2026 © The AvaloniaUI Project | https://github.com/AvaloniaUI/Avalonia |
| Avalonia.Fonts.Inter | 12.1.4 | MIT (package). The embedded Inter typeface is published by The Inter Project Authors under the SIL Open Font License 1.1 — **unverified from the package** (the package contains no font license file) | Copyright 2013-2026 © The AvaloniaUI Project | https://github.com/rsms/inter |
| Avalonia.AvaloniaEdit | 12.0.0 | MIT | Copyright 2017-2026 © The AvaloniaUI Project | https://github.com/AvaloniaUI/AvaloniaEdit |
| Avalonia.Angle.Windows.Natives (transitive, Windows) | 2.1.27548.20260419 | BSD-3-Clause style (ANGLE), see [Appendix C](#appendix-c-angle-license-avaloniaanglewindowsnatives) | Copyright 2018 The ANGLE Project Authors | https://chromium.googlesource.com/angle/angle |
| Avalonia.BuildServices (transitive, build-time) | 11.3.2 | MIT | Avalonia Team | https://avaloniaui.net |
| CommunityToolkit.Mvvm | 8.4.2 | MIT (package also carries `ThirdPartyNotices.txt`) | (c) .NET Foundation and Contributors | https://github.com/CommunityToolkit/dotnet |
| Dock.Avalonia, Dock.Avalonia.Themes.Fluent, Dock.Model.Mvvm and transitive Dock.* packages | 12.1.0.12 | MIT | Copyright © Wiesław Šoltés 2026 | https://github.com/wieslawsoltes/Dock |
| Antlr4.Runtime.Standard | 4.13.1 | BSD-3-Clause | Copyright (c) 2012-2020 The ANTLR Project | https://github.com/antlr/antlr4 |
| SkiaSharp and SkiaSharp.NativeAssets.* (transitive) | 3.119.4 | MIT (managed). The native Skia library bundles many components; their notices ship as `licenses/SkiaSharp-THIRD-PARTY-NOTICES.txt` | Copyright (c) 2015-2016 Xamarin, Inc.; Copyright (c) 2017-2018 Microsoft Corporation | https://github.com/mono/SkiaSharp |
| HarfBuzzSharp and HarfBuzzSharp.NativeAssets.* (transitive) | 8.3.1.3 | MIT (managed). Native HarfBuzz notices ship as `licenses/SkiaSharp-THIRD-PARTY-NOTICES.txt` (the HarfBuzzSharp native packages carry the same file) | © Microsoft Corporation | https://github.com/mono/SkiaSharp |
| MicroCom.Runtime (transitive) | 0.11.6 | MIT | Copyright 2021 © Nikita Tsukanov | https://github.com/kekekeks/MicroCom |
| Tmds.DBus.Protocol (transitive, Linux only code path) | 0.94.1 | MIT | Tom Deseyn | https://github.com/tmds/Tmds.DBus |
| Oracle.ManagedDataAccess.Core | 23.26.301 | Oracle Free Distribution, Hosting, and Use Terms (see above) | Oracle and/or its affiliates | https://www.oracle.com/database/technologies/appdev/dotnet.html |
| System.Security.Cryptography.Pkcs, System.Security.Cryptography.ProtectedData | 10.0.13 | MIT | © Microsoft Corporation | https://github.com/dotnet/runtime |
| System.Configuration.ConfigurationManager, System.Diagnostics.EventLog, System.Diagnostics.PerformanceCounter (transitive) | 8.0.0 | MIT | © Microsoft Corporation | https://github.com/dotnet/runtime |
| System.DirectoryServices.Protocols (transitive) | 8.0.2 | MIT | © Microsoft Corporation | https://github.com/dotnet/runtime |
| Microsoft.Extensions.DependencyInjection.Abstractions, Microsoft.Extensions.Logging.Abstractions (transitive) | 8.0.0 | MIT | © Microsoft Corporation | https://github.com/dotnet/runtime |
| Microsoft.IO.RecyclableMemoryStream (transitive) | 3.0.1 | MIT | © Microsoft Corporation | https://github.com/microsoft/Microsoft.IO.RecyclableMemoryStream |
| Microsoft.Data.Sqlite, Microsoft.Data.Sqlite.Core | 10.0.0 | MIT | © Microsoft Corporation | https://github.com/dotnet/efcore |
| SQLitePCLRaw.lib.e_sqlite3 | 2.1.14 | Apache-2.0, see [Appendix D](#appendix-d-apache-license-20) | Copyright 2014-2024 SourceGear, LLC | https://github.com/ericsink/SQLitePCL.raw |
| SQLitePCLRaw.bundle_e_sqlite3, SQLitePCLRaw.core, SQLitePCLRaw.provider.e_sqlite3 (transitive) | 2.1.11 | Apache-2.0 | Eric Sink | https://github.com/ericsink/SQLitePCL.raw |
| SQLite (native library inside SQLitePCLRaw.lib.e_sqlite3) | — | Public domain (per sqlite.org; not stated in the package) | — | https://sqlite.org/copyright.html |
| Velopack | 1.2.161 | MIT | Copyright © Velopack Ltd. | https://github.com/velopack/velopack |
| Velopack `Update.exe` / `Setup.exe` stubs (added by `vpk pack`, tool version 1.2.161) | 1.2.161 | MIT (same project) | Velopack Ltd. | https://github.com/velopack/velopack |
| .NET runtime (self-contained publish) | 10.0 | MIT; the runtime's own third-party notices are at https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT | © .NET Foundation and Contributors | https://github.com/dotnet/runtime |

## Not shipped

| Package | Version | License | Why it is not shipped |
|---|---|---|---|
| AvaloniaUI.DiagnosticsSupport | 2.2.3 | **Unverified** — the `.nuspec` has no `license` element and the package has no license file (Copyright 2019-2026 © AvaloniaUI OÜ) | Referenced only in `Debug` builds (`Condition="'$(Configuration)' == 'Debug'"`); release packages are built in `Release` |
| Antlr4BuildTasks | 12.14.0 | MIT (Ken Domino) | Build-time only (`PrivateAssets="all" IncludeAssets="build"`); generates parser code at build time |

## Source code from other projects in this repository

| Files | License | Copyright |
|---|---|---|
| `src/Oranda.Language/Parsing/Grammar/PlSqlLexer.g4`, `PlSqlParser.g4` (from the ANTLR grammars-v4 project) | Apache-2.0 (header kept in each file), see [Appendix D](#appendix-d-apache-license-20) | Copyright (c) 2009-2011 Alexandre Porcelli; Copyright (c) 2015-2019 Ivan Kochurkin (Positive Technologies); Copyright (c) 2017-2018 Mark Adams |

## MIT License text

Applies to every package marked MIT above, with the copyright line of that package.

```text
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## BSD 3-Clause License text (ANTLR 4 runtime)

```text
Copyright (c) 2012-2020 The ANTLR Project. All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions
are met:

1. Redistributions of source code must retain the above copyright
   notice, this list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright
   notice, this list of conditions and the following disclaimer in the
   documentation and/or other materials provided with the distribution.

3. Neither name of copyright holders nor the names of its contributors
   may be used to endorse or promote products derived from this software
   without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
``AS IS'' AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE REGENTS OR
CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL,
EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO,
PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR
PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF
LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING
NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## Appendix A. Oracle Free Distribution, Hosting, and Use Terms and Conditions

Verbatim copy of `LICENSE.txt` from the `Oracle.ManagedDataAccess.Core` 23.26.301 NuGet package. This copy is included because the license requires it with any distribution of the Program.

```text
Your use of this Program is governed by the Oracle Free Distribution, Hosting, and Use Terms and Conditions set forth below, unless you have received this Program (alone or as part of another Oracle product) under an Oracle license agreement (including but not limited to the Oracle Master Agreement), in which case your use of this Program is governed solely by such license agreement with Oracle.

Oracle Free Distribution, Hosting, and Use Terms and Conditions
Definitions
"Oracle" refers to Oracle America, Inc. "You" and "Your" refers to (a) a company or organization (each an "Entity") accessing the Programs, if use of the Programs will be on behalf of such Entity; or (b) an individual accessing the Programs, if use of the Programs will not be on behalf of an Entity. "Program(s)" refers to Oracle software provided by Oracle pursuant to the following terms and any updates, error corrections, and/or Program Documentation provided by Oracle. "Program Documentation" refers to Program user manuals and Program installation manuals, if any. If available, Program Documentation may be delivered with the Programs and/or may be accessed from www.oracle.com/documentation. "Separate Terms" refers to separate license terms that are specified in the Program Documentation, readmes or notice files and that apply to Separately Licensed Technology. "Separately Licensed Technology" refers to Oracle or third party technology that is licensed under Separate Terms and not under the terms of this license.

Separately Licensed Technology
Oracle may provide certain notices to You in Program Documentation, readmes or notice files in connection with Oracle or third party technology provided as or with the Programs. If specified in the Program Documentation, readmes or notice files, such technology will be licensed to You under Separate Terms. Your rights to use Separately Licensed Technology under Separate Terms are not restricted in any way by the terms herein. For clarity, notwithstanding the existence of a notice, third party technology that is not Separately Licensed Technology shall be deemed part of the Programs licensed to You under the terms of this license.

Source Code for Open Source Software
For software that You receive from Oracle in binary form that is licensed under an open source license that gives You the right to receive the source code for that binary, You can obtain a copy of the applicable source code from https://oss.oracle.com/sources/ or http://www.oracle.com/goto/opensourcecode. If the source code for such software was not provided to You with the binary, You can also receive a copy of the source code on physical media by submitting a written request pursuant to the instructions in the "Written Offer for Source Code" section of the latter website.

-------------------------------------------------------------------------------
The following license terms apply to those Programs that are not provided to You under Separate Terms.
License Rights and Restrictions
Oracle grants to You, as a recipient of this Program, a nonexclusive, nontransferable, limited license to, subject to the conditions stated herein, use the unmodified Programs, including, without limitation, for the purposes of:
•	developing, testing, prototyping and demonstrating applications; 
•	running the unmodified Programs for training, personal use, your business operations, and the business operations of third parties;
•	making the unmodified Programs available for use by third parties in your hosted environment and in cloud services; 
•	redistributing unmodified Programs and Programs Documentation under the terms of this License; and
•	copying the unmodified Programs and Program Documentation to the extent reasonably necessary to exercise the license rights granted herein and for backup purposes.
For the purposes of this license, compiling, interpreting or configuring an otherwise unmodified Program as necessary to run the Program shall not be considered modification.

Your license is contingent on Your compliance with the following conditions:
- You include a copy of this license with any distribution by You of the Programs;
- You do not charge your customers, end users, distributees or other third parties any additional fees for the distribution or use of the Programs; however, for clarity, if you comply with the foregoing condition, distribution or use of the Program as part of your for-fee product or service that adds substantial additional value is permitted; 
- You do not remove markings or notices of either Oracle's or a licensor's proprietary rights from the Programs or Program Documentation;
- You comply with all U.S. and applicable export control and economic sanctions laws and regulations that govern Your use of the Programs (including technical data); and
- You do not cause or permit reverse engineering, disassembly or decompilation of the Programs (except as allowed by law) by You nor allow an associated party to do so.
Any source code that may be included in the distribution with the Programs may not be modified, unless such source code is under Separate Terms permitting modification.
Ownership
Oracle or its licensors retain all ownership and intellectual property rights to the Programs.

Information Collection
The Programs' installation and/or auto-update processes, if any, may transmit a limited amount of data to Oracle or its service provider about those processes to help Oracle understand and optimize them. Oracle does not associate the data with personally identifiable information. Refer to Oracle's Privacy Policy at www.oracle.com/privacy.

Disclaimer of Warranties; Limitation of Liability
THE PROGRAMS ARE PROVIDED "AS IS" WITHOUT WARRANTY OF ANY KIND. ORACLE FURTHER DISCLAIMS ALL WARRANTIES, EXPRESS AND IMPLIED, INCLUDING WITHOUT LIMITATION, ANY IMPLIED WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE, OR NONINFRINGEMENT.
IN NO EVENT UNLESS REQUIRED BY APPLICABLE LAW WILL ORACLE BE LIABLE TO YOU FOR DAMAGES, INCLUDING ANY GENERAL, SPECIAL, INCIDENTAL OR CONSEQUENTIAL DAMAGES ARISING OUT OF THE USE OR INABILITY TO USE THE PROGRAM (INCLUDING BUT NOT LIMITED TO LOSS OF DATA OR DATA BEING RENDERED INACCURATE OR LOSSES SUSTAINED BY YOU OR THIRD PARTIES OR A FAILURE OF THE PROGRAM TO OPERATE WITH ANY OTHER PROGRAMS), EVEN IF SUCH HOLDER OR OTHER PARTY HAS BEEN ADVISED OF THE POSSIBILITY OF SUCH DAMAGES.

Version 1.0 
Last updated:  28 June 2022
```

## Appendix B. Oracle Data Provider for .NET: separately licensed technology notices

Verbatim copy of `info.txt` from the `Oracle.ManagedDataAccess.Core` 23.26.301 NuGet package (MIT Kerberos and related notices).

```text
The following software may be included in Oracle Data Provider for .NET:

Kerberos

Copyright (C) 1985-2010 by the Massachusetts Institute of Technology.

All rights reserved.

Export of this software from the United States of America may require a specific license from the United States Government. It is the responsibility of any person or organization contemplating export to obtain such a license before exporting.

WITHIN THAT CONSTRAINT, permission to use, copy, modify, and distribute this software and its documentation for any purpose and without fee is hereby granted, provided that the above copyright notice appear in all copies and that both that copyright notice and this permission notice appear in supporting documentation, and that the name of M.I.T. not be used in advertising or publicity pertaining to distribution of the software without specific, written prior permission. Furthermore if you modify this software you must label your software as modified software and not distribute it in such a fashion that it might be confused with the original MIT software. M.I.T. makes no representations about the suitability of this software for any purpose. It is provided "as is" without express or implied warranty.

THIS SOFTWARE IS PROVIDED "AS IS" AND WITHOUT ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, WITHOUT LIMITATION, THE IMPLIED WARRANTIES OF MERCHANTIBILITY AND FITNESS FOR A PARTICULAR PURPOSE.

Individual source code files are copyright MIT, Cygnus Support, Novell, OpenVision Technologies, Oracle, Red Hat, Sun Microsystems, FundsXpress, and others.

Project Athena, Athena, Athena MUSE, Discuss, Hesiod, Kerberos, Moira, and Zephyr are trademarks of the Massachusetts Institute of Technology (MIT). No commercial use of these trademarks may be made without prior written permission of MIT.

"Commercial use" means use of a name in a product or other for-profit manner. It does NOT prevent a commercial firm from referring to the MIT trademarks in order to convey information (although in doing so, recognition of their trademark status should be given).

-----------------------------------------

Portions of src/lib/crypto have the following copyright:

Copyright (C) 1998 by the FundsXpress, INC.

All rights reserved.

Export of this software from the United States of America may require a specific license from the United States Government. It is the responsibility of any person or organization contemplating export to obtain such a license before exporting.

WITHIN THAT CONSTRAINT, permission to use, copy, modify, and distribute this software and its documentation for any purpose and without fee is hereby granted, provided that the above copyright notice appear in all copies and that both that copyright notice and this permission notice appear in supporting documentation, and that the name of FundsXpress. not be used in advertising or publicity pertaining to distribution of the software without specific, written prior permission. FundsXpress makes no representations about the suitability of this software for any purpose. It is provided "as is" without express or implied warranty.

THIS SOFTWARE IS PROVIDED "AS IS" AND WITHOUT ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, WITHOUT LIMITATION, THE IMPLIED WARRANTIES OF MERCHANTIBILITY AND FITNESS FOR A PARTICULAR PURPOSE.

-----------------------------------------

The following copyright and permission notice applies to the OpenVision Kerberos Administration system located in kadmin/create, kadmin/dbutil, kadmin/passwd, kadmin/server, lib/kadm5, and portions of lib/rpc:

Copyright, OpenVision Technologies, Inc., 1996, All Rights Reserved

WARNING: Retrieving the OpenVision Kerberos Administration system source code, as described below, indicates your acceptance of the following terms. If you do not agree to the following terms, do not retrieve the OpenVision Kerberos administration system.

You may freely use and distribute the Source Code and Object Code compiled from it, with or without modification, but this Source Code is provided to you "AS IS" EXCLUSIVE OF ANY WARRANTY, INCLUDING, WITHOUT LIMITATION, ANY WARRANTIES OF MERCHANTABILITY OR FITNESS FOR A PARTICULAR PURPOSE, OR ANY OTHER WARRANTY, WHETHER EXPRESS OR IMPLIED. IN NO EVENT WILL OPENVISION HAVE ANY LIABILITY FOR ANY LOST PROFITS, LOSS OF DATA OR COSTS OF PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES, OR FOR ANY SPECIAL, INDIRECT, OR CONSEQUENTIAL DAMAGES ARISING OUT OF THIS AGREEMENT, INCLUDING, WITHOUT LIMITATION, THOSE RESULTING FROM THE USE OF THE SOURCE CODE, OR THE FAILURE OF THE SOURCE CODE TO PERFORM, OR FOR ANY OTHER REASON.

OpenVision retains all copyrights in the donated Source Code. OpenVision also retains copyright to derivative works of the Source Code, whether created by OpenVision or by a third party. The OpenVision copyright notice must be preserved if derivative works are made based on the donated Source Code.

OpenVision Technologies, Inc. has donated this Kerberos Administration system to MIT for inclusion in the standard Kerberos 5 distribution. This donation underscores our commitment to continuing Kerberos technology development and our gratitude for the valuable work which has been performed by MIT and the Kerberos community.

-----------------------------------------

Portions contributed by Matt Crawford <crawdad@fnal.gov> were work performed at Fermi National Accelerator Laboratory, which is operated by Universities Research Association, Inc., under contract DE-AC02-76CHO3000 with the U.S. Department of Energy.

-----------------------------------------

The implementation of the Yarrow pseudo-random number generator in src/lib/crypto/yarrow has the following copyright:

Copyright 2000 by Zero-Knowledge Systems, Inc.

Permission to use, copy, modify, distribute, and sell this software and its documentation for any purpose is hereby granted without fee, provided that the above copyright notice appear in all copies and that both that copyright notice and this permission notice appear in supporting documentation, and that the name of Zero-Knowledge Systems, Inc. not be used in advertising or publicity pertaining to distribution of the software without specific, written prior permission. Zero-Knowledge Systems, Inc. makes no representations about the suitability of this software for any purpose. It is provided "as is" without express or implied warranty.

ZERO-KNOWLEDGE SYSTEMS, INC. DISCLAIMS ALL WARRANTIES WITH REGARD TO THIS SOFTWARE, INCLUDING ALL IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS, IN NO EVENT SHALL ZERO-KNOWLEDGE SYSTEMS, INC. BE LIABLE FOR ANY SPECIAL, INDIRECT OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN AN ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTUOUS ACTION, ARISING OUT OF OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THIS SOFTWARE.

-----------------------------------------

The implementation of the AES encryption algorithm in src/lib/crypto/aes has the following copyright:

Copyright (c) 2001, Dr Brian Gladman <brg@gladman.uk.net>, Worcester, UK. All rights reserved.

LICENSE TERMS

The free distribution and use of this software in both source and binary form is allowed (with or without changes) provided that:

1. distributions of this source code include the above copyright notice, this list of conditions and the following disclaimer;

2. distributions in binary form include the above copyright notice, this list of conditions and the following disclaimer in the documentation and/or other associated materials;

3. the copyright holder's name is not used to endorse products built using this software without specific written permission.

DISCLAIMER

This software is provided 'as is' with no explicit or implied warranties in respect of any properties, including, but not limited to, correctness and fitness for purpose.

-----------------------------------------

Portions contributed by Red Hat, including the pre-authentication plug-ins framework, contain the following copyright:

Copyright (c) 2006 Red Hat, Inc.

Portions copyright (c) 2006 Massachusetts Institute of Technology

All Rights Reserved.

Redistribution and use in source and binary forms, with or without modification, are permitted provided that the following conditions are met:

    Redistributions of source code must retain the above copyright notice, this list of conditions and the following disclaimer.

    Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following disclaimer in the documentation and/or other materials provided with the distribution.

    Neither the name of Red Hat, Inc., nor the names of its contributors may be used to endorse or promote products derived from this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

-----------------------------------------

The implementations of GSSAPI mechglue in GSSAPI-SPNEGO in src/lib/gssapi, including the following files:

lib/gssapi/generic/gssapi_err_generic.et
lib/gssapi/mechglue/g_accept_sec_context.c
lib/gssapi/mechglue/g_acquire_cred.c
lib/gssapi/mechglue/g_canon_name.c
lib/gssapi/mechglue/g_compare_name.c
lib/gssapi/mechglue/g_context_time.c
lib/gssapi/mechglue/g_delete_sec_context.c
lib/gssapi/mechglue/g_dsp_name.c
lib/gssapi/mechglue/g_dsp_status.c
lib/gssapi/mechglue/g_dup_name.c
lib/gssapi/mechglue/g_exp_sec_context.c
lib/gssapi/mechglue/g_export_name.c
lib/gssapi/mechglue/g_glue.c
lib/gssapi/mechglue/g_imp_name.c
lib/gssapi/mechglue/g_imp_sec_context.c
lib/gssapi/mechglue/g_init_sec_context.c
lib/gssapi/mechglue/g_initialize.c
lib/gssapi/mechglue/g_inquire_context.c
lib/gssapi/mechglue/g_inquire_cred.c
lib/gssapi/mechglue/g_inquire_names.c
lib/gssapi/mechglue/g_process_context.c
lib/gssapi/mechglue/g_rel_buffer.c
lib/gssapi/mechglue/g_rel_cred.c
lib/gssapi/mechglue/g_rel_name.c
lib/gssapi/mechglue/g_rel_oid_set.c
lib/gssapi/mechglue/g_seal.c
lib/gssapi/mechglue/g_sign.c
lib/gssapi/mechglue/g_store_cred.c
lib/gssapi/mechglue/g_unseal.c
lib/gssapi/mechglue/g_userok.c
lib/gssapi/mechglue/g_utils.c
lib/gssapi/mechglue/g_verify.c
lib/gssapi/mechglue/gssd_pname_to_uid.c
lib/gssapi/mechglue/mglueP.h
lib/gssapi/mechglue/oid_ops.c
lib/gssapi/spnego/gssapiP_spnego.h
lib/gssapi/spnego/spnego_mech.c

and the initial implementation of incremental propagation, including the following new or changed files:

include/iprop_hdr.h
kadmin/server/ipropd_svc.c
lib/kdb/iprop.x
lib/kdb/kdb_convert.c
lib/kdb/kdb_log.clib/kdb/kdb_log.h
lib/krb5/error_tables/kdb5_err.et
slave/kpropd_rpc.c
slave/kproplog.c

and marked portions of the following files:

lib/krb5/os/hst_realm.c

are subject to the following license:

Copyright (c) 2004 Sun Microsystems, Inc.

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

-----------------------------------------

MIT Kerberos includes documentation and software developed at the University of California at Berkeley, which includes this copyright notice:

Copyright (C) 1983 Regents of the University of California.

All rights reserved.

Redistribution and use in source and binary forms, with or without modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following disclaimer in the documentation and/or other materials provided with the distribution.

3. Neither the name of the University nor the names of its contributors may be used to endorse or promote products derived from this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE REGENTS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE REGENTS OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

-----------------------------------------

Portions contributed by Novell, Inc., including the LDAP database backend, are subject to the following license:

Copyright (c) 2004-2005, Novell, Inc. All rights reserved.

Redistribution and use in source and binary forms, with or without modification, are permitted provided that the following conditions are met:

    Redistributions of source code must retain the above copyright notice, this list of conditions and the following disclaimer.

    Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following disclaimer in the documentation and/or other materials provided with the distribution.

    The copyright holder's name is not used to endorse or promote products derived from this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

-----------------------------------------

Portions funded by Sandia National Laboratory and developed by the University of Michigan's Center for Information Technology Integration, including the PKINIT implementation, are subject to the following license:

COPYRIGHT (C) 2006-2007

THE REGENTS OF THE UNIVERSITY OF MICHIGAN

ALL RIGHTS RESERVED

Permission is granted to use, copy, create derivative works and redistribute this software and such derivative works for any purpose, so long as the name of The University of Michigan is not used in any advertising or publicity pertaining to the use of distribution of this software without specific, written prior authorization. If the above copyright notice or any other identification of the University of Michigan is included in any copy of any portion of this software, then the disclaimer below must also be included.

THIS SOFTWARE IS PROVIDED AS IS, WITHOUT REPRESENTATION FROM THE UNIVERSITY OF MICHIGAN AS TO ITS FITNESS FOR ANY PURPOSE, AND WITHOUT WARRANTY BY THE UNIVERSITY OF MICHIGAN OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE. THE REGENTS OF THE UNIVERSITY OF MICHIGAN SHALL NOT BE LIABLE FOR ANY DAMAGES, INCLUDING SPECIAL, INDIRECT, INCIDENTAL, OR CONSEQUENTIAL DAMAGES, WITH RESPECT TO ANY CLAIM ARISING OUT OF OR IN CONNECTION WITH THE USE OF THE SOFTWARE, EVEN IF IT HAS BEEN OR IS HEREAFTER ADVISED OF THE POSSIBILITY OF SUCH DAMAGES.

-----------------------------------------

The pkcs11.h file included in the PKINIT code has the following license:

Copyright 2006 g10 Code GmbH

Copyright 2006 Andreas Jellinghaus

This file is free software; as a special exception the author gives unlimited permission to copy and/or distribute it, with or without modifications, as long as this notice is preserved.

This file is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY, to the extent permitted by law; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.

-----------------------------------------

Portions contributed by Apple Inc. are subject to the following license:

Copyright 2004-2008 Apple Inc. All Rights Reserved.

Export of this software from the United States of America may require a specific license from the United States Government. It is the responsibility of any person or organization contemplating export to obtain such a license before exporting.

WITHIN THAT CONSTRAINT, permission to use, copy, modify, and distribute this software and its documentation for any purpose and without fee is hereby granted, provided that the above copyright notice appear in all copies and that both that copyright notice and this permission notice appear in supporting documentation, and that the name of Apple Inc. not be used in advertising or publicity pertaining to distribution of the software without specific, written prior permission. Apple Inc. makes no representations about the suitability of this software for any purpose. It is provided "as is" without express or implied warranty.

THIS SOFTWARE IS PROVIDED "AS IS'' AND WITHOUT ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, WITHOUT LIMITATION, THE IMPLIED WARRANTIES OF MERCHANTIBILITY AND FITNESS FOR A PARTICULAR PURPOSE.

-----------------------------------------

The implementations of strlcpy and strlcat in src/util/support/strlcat.c have the following copyright and permission notice:

Copyright (c) 1998 Todd C. Miller <Todd.Miller@courtesan.com>

Permission to use, copy, modify, and distribute this software for any purpose with or without fee is hereby granted, provided that the above copyright notice and this permission notice appear in all copies.

THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES WITH REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR ANY SPECIAL, DIRECT, INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN AN ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THIS SOFTWARE.

-----------------------------------------

The implementations of UTF-8 string handling in src/util/support and src/lib/krb5/unicode are subject to the following copyright and permission notice:

The OpenLDAP Public License

Version 2.8, 17 August 2003

Redistribution and use of this software and associated documentation ("Software"), with or without modification, are permitted provided that the following conditions are met:

    Redistributions in source form must retain copyright statements and notices,

    Redistributions in binary form must reproduce applicable copyright statements and notices, this list of conditions, and the following disclaimer in the documentation and/or other materials provided with the distribution, and

    Redistributions must contain a verbatim copy of this document.

The OpenLDAP Foundation may revise this license from time to time. Each revision is distinguished by a version number. You may use this Software under terms of this license revision or under the terms of any subsequent revision of the license.

THIS SOFTWARE IS PROVIDED BY THE OPENLDAP FOUNDATION AND ITS CONTRIBUTORS "AS IS'' AND ANY EXPRESSED OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE OPENLDAP FOUNDATION, ITS CONTRIBUTORS, OR THE AUTHOR(S) OR OWNER(S) OF THE SOFTWARE BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

The names of the authors and copyright holders must not be used in advertising or otherwise to promote the sale, use or other dealing in this Software without specific, written prior permission. Title to copyright in this Software shall at all times remain with copyright holders.

OpenLDAP is a registered trademark of the OpenLDAP Foundation.

Copyright 1999-2003 The OpenLDAP Foundation, Redwood City, California, USA. All Rights Reserved. Permission to copy and distribute verbatim copies of this document is granted.

-----------------------------------------

Marked test programs in src/lib/krb5/krb have the following copyright:

Copyright (c) 2006 Kungliga Tekniska Högskolan

(Royal Institute of Technology, Stockholm, Sweden).

All rights reserved.

Redistribution and use in source and binary forms, with or without modification, are permitted provided that the following conditions are met:

    Redistributions of source code must retain the above copyright notice, this list of conditions and the following disclaimer.

    Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following disclaimer in the documentation and/or other materials provided with the distribution.

    Neither the name of KTH nor the names of its contributors may be used to endorse or promote products derived from this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY KTH AND ITS CONTRIBUTORS "AS IS'' AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL KTH OR ITS CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## Appendix C. ANGLE license (Avalonia.Angle.Windows.Natives)

Verbatim copy of `LICENSE` from the `Avalonia.Angle.Windows.Natives` 2.1.27548.20260419 NuGet package.

```text
// Copyright 2018 The ANGLE Project Authors.
// All rights reserved.
//
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions
// are met:
//
//     Redistributions of source code must retain the above copyright
//     notice, this list of conditions and the following disclaimer.
//
//     Redistributions in binary form must reproduce the above
//     copyright notice, this list of conditions and the following
//     disclaimer in the documentation and/or other materials provided
//     with the distribution.
//
//     Neither the name of TransGaming Inc., Google Inc., 3DLabs Inc.
//     Ltd., nor the names of their contributors may be used to endorse
//     or promote products derived from this software without specific
//     prior written permission.
//
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
// "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
// LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS
// FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE
// COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT,
// INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING,
// BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
// LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
// CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT
// LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN
// ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
// POSSIBILITY OF SUCH DAMAGE.
```

## Appendix D. Apache License 2.0

Applies to SQLitePCLRaw and to the PL/SQL grammar files (`src/Oranda.Language/Parsing/Grammar/PlSqlLexer.g4`, `PlSqlParser.g4`). Text as shipped in the SkiaSharp third-party notices.

```text
Apache License

Version 2.0, January 2004

http://www.apache.org/licenses/

TERMS AND CONDITIONS FOR USE, REPRODUCTION, AND DISTRIBUTION

1. Definitions.

"License" shall mean the terms and conditions for use, reproduction, and
distribution as defined by Sections 1 through 9 of this document.

"Licensor" shall mean the copyright owner or entity authorized by the
copyright owner that is granting the License.

"Legal Entity" shall mean the union of the acting entity and all other
entities that control, are controlled by, or are under common control with
that entity. For the purposes of this definition, "control" means (i) the
power, direct or indirect, to cause the direction or management of such
entity, whether by contract or otherwise, or (ii) ownership of fifty
percent (50%) or more of the outstanding shares, or (iii) beneficial
ownership of such entity.

"You" (or "Your") shall mean an individual or Legal Entity exercising
permissions granted by this License.

"Source" form shall mean the preferred form for making modifications,
including but not limited to software source code, documentation
source, and configuration files.

"Object" form shall mean any form resulting from mechanical transformation
or translation of a Source form, including but not limited to compiled
object code, generated documentation, and conversions to other media types.

"Work" shall mean the work of authorship, whether in Source or Object
form, made available under the License, as indicated by a copyright
notice that is included in or attached to the work (an example is
provided in the Appendix below).

"Derivative Works" shall mean any work, whether in Source or Object
form, that is based on (or derived from) the Work and for which the
editorial revisions, annotations, elaborations, or other modifications
represent, as a whole, an original work of authorship. For the purposes
of this License, Derivative Works shall not include works that remain
separable from, or merely link (or bind by name) to the interfaces of,
the Work and Derivative Works thereof.

"Contribution" shall mean any work of authorship, including the original
version of the Work and any modifications or additions to that Work or
Derivative Works thereof, that is intentionally submitted to Licensor
for inclusion in the Work by the copyright owner or by an individual or
Legal Entity authorized to submit on behalf of the copyright owner. For
the purposes of this definition, "submitted" means any form of electronic,
verbal, or written communication sent to the Licensor or its
representatives, including but not limited to communication on electronic
mailing lists, source code control systems, and issue tracking systems that
are managed by, or on behalf of, the Licensor for the purpose of discussing
and improving the Work, but excluding communication that is conspicuously
marked or otherwise designated in writing by the copyright owner as "Not
a Contribution."

"Contributor" shall mean Licensor and any individual or Legal Entity on
behalf of whom a Contribution has been received by Licensor and subsequently
incorporated within the Work.

2. Grant of Copyright License. Subject to the terms and conditions of this
License, each Contributor hereby grants to You a perpetual, worldwide,
non-exclusive, no-charge, royalty-free, irrevocable copyright license to
reproduce, prepare Derivative Works of, publicly display, publicly perform,
sublicense, and distribute the Work and such Derivative Works in Source or
Object form.

3. Grant of Patent License. Subject to the terms and conditions of this
License, each Contributor hereby grants to You a perpetual, worldwide,
non-exclusive, no-charge, royalty-free, irrevocable (except as stated in
this section) patent license to make, have made, use, offer to sell, sell,
import, and otherwise transfer the Work, where such license applies only to
those patent claims licensable by such Contributor that are necessarily
infringed by their Contribution(s) alone or by combination of their
Contribution(s) with the Work to which such Contribution(s) was submitted.
If You institute patent litigation against any entity (including a cross-claim
or counterclaim in a lawsuit) alleging that the Work or a Contribution
incorporated within the Work constitutes direct or contributory patent
infringement, then any patent licenses granted to You under this License
for that Work shall terminate as of the date such litigation is filed.

4. Redistribution. You may reproduce and distribute copies of the Work or
Derivative Works thereof in any medium, with or without modifications, and
in Source or Object form, provided that You meet the following conditions:

You must give any other recipients of the Work or Derivative Works a copy of
this License; and
You must cause any modified files to carry prominent notices stating that
You changed the files; and
You must retain, in the Source form of any Derivative Works that You
distribute, all copyright, patent, trademark, and attribution notices
from the Source form of the Work, excluding those notices that do not
pertain to any part of the Derivative Works; and
If the Work includes a "NOTICE" text file as part of its distribution,
then any Derivative Works that You distribute must include a readable
copy of the attribution notices contained within such NOTICE file, excluding
those notices that do not pertain to any part of the Derivative Works, in
at least one of the following places: within a NOTICE text file distributed
as part of the Derivative Works; within the Source form or documentation, if
provided along with the Derivative Works; or, within a display generated by
the Derivative Works, if and wherever such third-party notices normally
appear. The contents of the NOTICE file are for informational purposes
only and do not modify the License. You may add Your own attribution
notices within Derivative Works that You distribute, alongside or as
an addendum to the NOTICE text from the Work, provided that such additional
attribution notices cannot be construed as modifying the License.

You may add Your own copyright statement to Your modifications and may provide
additional or different license terms and conditions for use, reproduction, or
distribution of Your modifications, or for any such Derivative Works as a
whole, provided Your use, reproduction, and distribution of the Work otherwise
complies with the conditions stated in this License.
5. Submission of Contributions. Unless You explicitly state otherwise, any
Contribution intentionally submitted for inclusion in the Work by You to the
Licensor shall be under the terms and conditions of this License, without any
additional terms or conditions. Notwithstanding the above, nothing herein
shall supersede or modify the terms of any separate license agreement you
may have executed with Licensor regarding such Contributions.

6. Trademarks. This License does not grant permission to use the trade names,
trademarks, service marks, or product names of the Licensor, except as
required for reasonable and customary use in describing the origin of the
Work and reproducing the content of the NOTICE file.

7. Disclaimer of Warranty. Unless required by applicable law or agreed to
in writing, Licensor provides the Work (and each Contributor provides its
Contributions) on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF
ANY KIND, either express or implied, including, without limitation, any
warranties or conditions of TITLE, NON-INFRINGEMENT, MERCHANTABILITY, or
FITNESS FOR A PARTICULAR PURPOSE. You are solely responsible for determining
the appropriateness of using or redistributing the Work and assume any risks
associated with Your exercise of permissions under this License.

8. Limitation of Liability. In no event and under no legal theory, whether in
tort (including negligence), contract, or otherwise, unless required by
applicable law (such as deliberate and grossly negligent acts) or agreed to
in writing, shall any Contributor be liable to You for damages, including
any direct, indirect, special, incidental, or consequential damages of any
character arising as a result of this License or out of the use or inability
to use the Work (including but not limited to damages for loss of goodwill,
work stoppage, computer failure or malfunction, or any and all other
commercial damages or losses), even if such Contributor has been advised
of the possibility of such damages.

9. Accepting Warranty or Additional Liability. While redistributing the
Work or Derivative Works thereof, You may choose to offer, and charge a
fee for, acceptance of support, warranty, indemnity, or other liability
obligations and/or rights consistent with this License. However, in accepting
such obligations, You may act only on Your own behalf and on Your sole
responsibility, not on behalf of any other Contributor, and only if You
agree to indemnify, defend, and hold each Contributor harmless for any
liability incurred by, or claims asserted against, such Contributor by
reason of your accepting any such warranty or additional liability.

END OF TERMS AND CONDITIONS
```
