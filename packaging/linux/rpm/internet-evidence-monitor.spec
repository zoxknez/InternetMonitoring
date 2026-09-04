Name:           internet-evidence-monitor
Version:        3.1.0
Release:        0.1.alpha1%{?dist}
Summary:        Local internet continuity monitor and evidence package
License:        MIT
URL:            https://github.com/zoxknez/InternetMonitoring
Source0:        iem-payload.tar.gz
BuildArch:      %{_arch}
Requires:       libX11, libICE, libSM, fontconfig, ca-certificates, tzdata, systemd
Requires(pre):  shadow-utils
Requires(post): systemd
Requires(preun): systemd
Requires(postun): systemd

%description
Internet Evidence Monitor records local network continuity observations and creates a
verifiable evidence package. It includes an Avalonia desktop application and a hardened
systemd service.

%prep
rm -rf %{_builddir}/iem-payload
mkdir -p %{_builddir}/iem-payload
tar -xzf %{SOURCE0} -C %{_builddir}/iem-payload

%build

%install
rm -rf %{buildroot}
mkdir -p %{buildroot}
cp -a %{_builddir}/iem-payload/. %{buildroot}/

%pre
getent group iem-users >/dev/null || groupadd --system iem-users
getent group iem-admin >/dev/null || groupadd --system iem-admin
getent group iem >/dev/null || groupadd --system iem
getent passwd iem >/dev/null || useradd --system --gid iem --groups iem-users \
    --home-dir /var/lib/internet-evidence-monitor --shell /usr/sbin/nologin iem
exit 0

%post
%systemd_post internet-evidence-monitor.service
IEM_GID="$(getent group iem | cut -d: -f3)"
if [ -n "${IEM_GID}" ]; then
    cat > /etc/sysctl.d/99-internet-evidence-monitor.conf <<EOF
# Managed by internet-evidence-monitor (rpm %post). Do not edit.
# Lets the iem service account open unprivileged ICMP datagram sockets
# without CAP_NET_RAW.
net.ipv4.ping_group_range = ${IEM_GID} ${IEM_GID}
EOF
    chmod 0644 /etc/sysctl.d/99-internet-evidence-monitor.conf
    if [ -d /run/systemd/system ] && command -v sysctl >/dev/null 2>&1; then
        sysctl --quiet -p /etc/sysctl.d/99-internet-evidence-monitor.conf || true
    fi
fi

%preun
%systemd_preun internet-evidence-monitor.service

%postun
%systemd_postun_with_restart internet-evidence-monitor.service
if [ "$1" = "0" ]; then
    # Full uninstall: the sysctl drop-in is ours. Evidence in /var/lib and the
    # iem / iem-users / iem-admin accounts are deliberately kept.
    rm -f /etc/sysctl.d/99-internet-evidence-monitor.conf
fi

%files
%license /usr/share/doc/internet-evidence-monitor/copyright
/usr/share/doc/internet-evidence-monitor/sbom.json
/usr/bin/internet-evidence-monitor
/usr/lib/internet-evidence-monitor/
/usr/share/applications/internet-evidence-monitor.desktop
/usr/share/icons/hicolor/scalable/apps/internet-evidence-monitor.svg
%{_unitdir}/internet-evidence-monitor.service

%changelog
* Sat Aug 29 2026 Internet Evidence Monitor contributors - 3.1.0-0.1.alpha1
- First Linux alpha with Avalonia UI and systemd service.
