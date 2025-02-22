﻿/*
Technitium DNS Server
Copyright (C) 2022  Shreyas Zare (shreyas@technitium.com)

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <http://www.gnu.org/licenses/>.

*/

using DnsServerCore.Dns.Dnssec;
using DnsServerCore.Dns.ResourceRecords;
using DnsServerCore.Dns.ZoneManagers;
using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using TechnitiumLibrary.Net.Dns;
using TechnitiumLibrary.Net.Dns.ResourceRecords;

namespace DnsServerCore.Dns.Zones
{
    public enum AuthZoneDnssecStatus : byte
    {
        Unsigned = 0,
        SignedWithNSEC = 1,
        SignedWithNSEC3 = 2,
    }

    class PrimaryZone : ApexZone
    {
        #region variables

        readonly DnsServer _dnsServer;
        readonly bool _internal;

        readonly List<DnsResourceRecord> _history; //for IXFR support
        IReadOnlyDictionary<string, object> _tsigKeyNames;

        readonly Timer _notifyTimer;
        bool _notifyTimerTriggered;
        const int NOTIFY_TIMER_INTERVAL = 10000;
        readonly List<NameServerAddress> _notifyList;

        const int NOTIFY_TIMEOUT = 10000;
        const int NOTIFY_RETRIES = 5;

        Dictionary<ushort, DnssecPrivateKey> _dnssecPrivateKeys;
        const uint DNSSEC_SIGNATURE_INCEPTION_OFFSET = 60 * 60;
        Timer _dnssecTimer;
        const int DNSSEC_TIMER_INITIAL_INTERVAL = 30000;
        const int DNSSEC_TIMER_PERIODIC_INTERVAL = 900000;
        DateTime _lastSignatureRefreshCheckedOn;
        readonly object _dnssecUpdateLock = new object();

        #endregion

        #region constructor

        public PrimaryZone(DnsServer dnsServer, AuthZoneInfo zoneInfo)
            : base(zoneInfo)
        {
            _dnsServer = dnsServer;

            if (zoneInfo.ZoneHistory is null)
                _history = new List<DnsResourceRecord>();
            else
                _history = new List<DnsResourceRecord>(zoneInfo.ZoneHistory);

            _tsigKeyNames = zoneInfo.TsigKeyNames;

            IReadOnlyCollection<DnssecPrivateKey> dnssecPrivateKeys = zoneInfo.DnssecPrivateKeys;
            if (dnssecPrivateKeys is not null)
            {
                _dnssecPrivateKeys = new Dictionary<ushort, DnssecPrivateKey>(dnssecPrivateKeys.Count);

                foreach (DnssecPrivateKey dnssecPrivateKey in dnssecPrivateKeys)
                    _dnssecPrivateKeys.Add(dnssecPrivateKey.KeyTag, dnssecPrivateKey);
            }

            _notifyTimer = new Timer(NotifyTimerCallback, null, Timeout.Infinite, Timeout.Infinite);
            _notifyList = new List<NameServerAddress>();
        }

        public PrimaryZone(DnsServer dnsServer, string name, string primaryNameServer, bool @internal)
            : base(name)
        {
            _dnsServer = dnsServer;
            _internal = @internal;

            if (_internal)
            {
                _zoneTransfer = AuthZoneTransfer.Deny;
                _notify = AuthZoneNotify.None;
            }
            else
            {
                _zoneTransfer = AuthZoneTransfer.AllowOnlyZoneNameServers;
                _notify = AuthZoneNotify.ZoneNameServers;

                _history = new List<DnsResourceRecord>();

                _notifyTimer = new Timer(NotifyTimerCallback, null, Timeout.Infinite, Timeout.Infinite);
                _notifyList = new List<NameServerAddress>();
            }

            DnsSOARecordData soa = new DnsSOARecordData(primaryNameServer, _name.Length == 0 ? "hostadmin" : "hostadmin." + _name, 1, 900, 300, 604800, 900);

            _entries[DnsResourceRecordType.SOA] = new DnsResourceRecord[] { new DnsResourceRecord(_name, DnsResourceRecordType.SOA, DnsClass.IN, soa.Minimum, soa) };
            _entries[DnsResourceRecordType.NS] = new DnsResourceRecord[] { new DnsResourceRecord(_name, DnsResourceRecordType.NS, DnsClass.IN, 3600, new DnsNSRecordData(soa.PrimaryNameServer)) };
        }

        internal PrimaryZone(DnsServer dnsServer, string name, DnsSOARecordData soa, DnsNSRecordData ns)
            : base(name)
        {
            _dnsServer = dnsServer;
            _internal = true;

            _zoneTransfer = AuthZoneTransfer.Deny;
            _notify = AuthZoneNotify.None;

            _entries[DnsResourceRecordType.SOA] = new DnsResourceRecord[] { new DnsResourceRecord(_name, DnsResourceRecordType.SOA, DnsClass.IN, soa.Minimum, soa) };
            _entries[DnsResourceRecordType.NS] = new DnsResourceRecord[] { new DnsResourceRecord(_name, DnsResourceRecordType.NS, DnsClass.IN, 3600, ns) };
        }

        #endregion

        #region IDisposable

        bool _disposed;

        protected override void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                if (_notifyTimer is not null)
                    _notifyTimer.Dispose();

                Timer dnssecTimer = _dnssecTimer;
                if (dnssecTimer is not null)
                {
                    lock (dnssecTimer)
                    {
                        dnssecTimer.Dispose();
                        _dnssecTimer = null;
                    }
                }
            }

            _disposed = true;
        }

        #endregion

        #region private

        private async void NotifyTimerCallback(object state)
        {
            try
            {
                switch (_notify)
                {
                    case AuthZoneNotify.ZoneNameServers:
                        IReadOnlyList<NameServerAddress> secondaryNameServers = await GetSecondaryNameServerAddressesAsync(_dnsServer);

                        foreach (NameServerAddress secondaryNameServer in secondaryNameServers)
                            _ = NotifyNameServerAsync(secondaryNameServer);

                        break;

                    case AuthZoneNotify.SpecifiedNameServers:
                        IReadOnlyCollection<IPAddress> specifiedNameServers = _notifyNameServers;
                        if (specifiedNameServers is not null)
                        {
                            foreach (IPAddress specifiedNameServer in specifiedNameServers)
                                _ = NotifyNameServerAsync(new NameServerAddress(specifiedNameServer));
                        }

                        break;

                    default:
                        return;
                }
            }
            catch (Exception ex)
            {
                LogManager log = _dnsServer.LogManager;
                if (log is not null)
                    log.Write(ex);
            }
            finally
            {
                _notifyTimerTriggered = false;
            }
        }

        private async Task NotifyNameServerAsync(NameServerAddress nameServer)
        {
            //use notify list to prevent multiple threads from notifying the same name server
            lock (_notifyList)
            {
                if (_notifyList.Contains(nameServer))
                    return; //already notifying the name server in another thread

                _notifyList.Add(nameServer);
            }

            try
            {
                DnsClient client = new DnsClient(nameServer);

                client.Proxy = _dnsServer.Proxy;
                client.Timeout = NOTIFY_TIMEOUT;
                client.Retries = NOTIFY_RETRIES;

                DnsDatagram notifyRequest = new DnsDatagram(0, false, DnsOpcode.Notify, true, false, false, false, false, false, DnsResponseCode.NoError, new DnsQuestionRecord[] { new DnsQuestionRecord(_name, DnsResourceRecordType.SOA, DnsClass.IN) }, _entries[DnsResourceRecordType.SOA]);
                DnsDatagram response = await client.ResolveAsync(notifyRequest);

                switch (response.RCODE)
                {
                    case DnsResponseCode.NoError:
                    case DnsResponseCode.NotImplemented:
                        {
                            //transaction complete
                            LogManager log = _dnsServer.LogManager;
                            if (log is not null)
                                log.Write("DNS Server successfully notified name server for '" + (_name == "" ? "<root>" : _name) + "' zone changes: " + nameServer.ToString());
                        }
                        break;

                    default:
                        {
                            //transaction failed
                            LogManager log = _dnsServer.LogManager;
                            if (log is not null)
                                log.Write("DNS Server received RCODE=" + response.RCODE.ToString() + " from name server for '" + (_name == "" ? "<root>" : _name) + "' zone notification: " + nameServer.ToString());
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                LogManager log = _dnsServer.LogManager;
                if (log is not null)
                {
                    log.Write("DNS Server failed to notify name server for '" + (_name == "" ? "<root>" : _name) + "' zone changes: " + nameServer.ToString());
                    log.Write(ex);
                }
            }
            finally
            {
                lock (_notifyList)
                {
                    _notifyList.Remove(nameServer);
                }
            }
        }

        #endregion

        #region DNSSEC

        internal override void UpdateDnssecStatus()
        {
            base.UpdateDnssecStatus();

            if (_dnssecStatus != AuthZoneDnssecStatus.Unsigned)
                _dnssecTimer = new Timer(DnssecTimerCallback, null, DNSSEC_TIMER_INITIAL_INTERVAL, Timeout.Infinite);
        }

        private async void DnssecTimerCallback(object state)
        {
            try
            {
                List<DnssecPrivateKey> kskToReady = null;
                List<DnssecPrivateKey> kskToActivate = null;
                List<DnssecPrivateKey> kskToRetire = null;
                List<DnssecPrivateKey> kskToRevoke = null;
                List<DnssecPrivateKey> kskToUnpublish = null;

                List<DnssecPrivateKey> zskToActivate = null;
                List<DnssecPrivateKey> zskToRetire = null;
                List<DnssecPrivateKey> zskToRollover = null;
                List<DnssecPrivateKey> zskToDeactivateAndUnpublish = null;

                uint dnsKeyTtl = GetDnsKeyTtl();
                bool saveZone = false;

                lock (_dnssecPrivateKeys)
                {
                    foreach (KeyValuePair<ushort, DnssecPrivateKey> privateKeyEntry in _dnssecPrivateKeys)
                    {
                        DnssecPrivateKey privateKey = privateKeyEntry.Value;

                        if (privateKey.KeyType == DnssecPrivateKeyType.KeySigningKey)
                        {
                            //KSK
                            switch (privateKey.State)
                            {
                                case DnssecPrivateKeyState.Published:
                                    if (DateTime.UtcNow > privateKey.StateChangedOn.AddSeconds(dnsKeyTtl))
                                    {
                                        //long enough time old RRset to expire from caches
                                        if (kskToReady is null)
                                            kskToReady = new List<DnssecPrivateKey>();

                                        kskToReady.Add(privateKey);

                                        if (kskToActivate is null)
                                            kskToActivate = new List<DnssecPrivateKey>();

                                        kskToActivate.Add(privateKey);
                                    }
                                    break;

                                case DnssecPrivateKeyState.Ready:
                                    if (privateKey.IsRetiring)
                                    {
                                        if (kskToRetire is null)
                                            kskToRetire = new List<DnssecPrivateKey>();

                                        kskToRetire.Add(privateKey);
                                    }
                                    else
                                    {
                                        if (kskToActivate is null)
                                            kskToActivate = new List<DnssecPrivateKey>();

                                        kskToActivate.Add(privateKey);
                                    }
                                    break;

                                case DnssecPrivateKeyState.Active:
                                    if (privateKey.IsRetiring)
                                    {
                                        if (kskToRetire is null)
                                            kskToRetire = new List<DnssecPrivateKey>();

                                        kskToRetire.Add(privateKey);
                                    }
                                    break;

                                case DnssecPrivateKeyState.Retired:
                                    if (DateTime.UtcNow > privateKey.StateChangedOn.AddSeconds(dnsKeyTtl))
                                    {
                                        //KSK needs to be revoked for RFC5011 consideration
                                        if (kskToRevoke is null)
                                            kskToRevoke = new List<DnssecPrivateKey>();

                                        kskToRevoke.Add(privateKey);
                                    }
                                    break;

                                case DnssecPrivateKeyState.Revoked:
                                    //rfc7583#section-3.3.4
                                    //modifiedQueryInterval = MAX(1hr, MIN(15 days, TTLkey / 2)) 
                                    uint modifiedQueryInterval = Math.Max(3600u, Math.Min(15 * 24 * 60 * 60, dnsKeyTtl / 2));

                                    if (DateTime.UtcNow > privateKey.StateChangedOn.AddSeconds(modifiedQueryInterval))
                                    {
                                        //key has been revoked for sufficient time
                                        if (kskToUnpublish is null)
                                            kskToUnpublish = new List<DnssecPrivateKey>();

                                        kskToUnpublish.Add(privateKey);
                                    }
                                    break;
                            }
                        }
                        else
                        {
                            //ZSK
                            switch (privateKey.State)
                            {
                                case DnssecPrivateKeyState.Published:
                                    if (DateTime.UtcNow > privateKey.StateChangedOn.AddSeconds(dnsKeyTtl))
                                    {
                                        //long enough time old RRset to expire from caches
                                        privateKey.SetState(DnssecPrivateKeyState.Ready);

                                        if (zskToActivate is null)
                                            zskToActivate = new List<DnssecPrivateKey>();

                                        zskToActivate.Add(privateKey);
                                    }
                                    break;

                                case DnssecPrivateKeyState.Ready:
                                    if (zskToActivate is null)
                                        zskToActivate = new List<DnssecPrivateKey>();

                                    zskToActivate.Add(privateKey);
                                    break;

                                case DnssecPrivateKeyState.Active:
                                    if (privateKey.IsRetiring)
                                    {
                                        if (zskToRetire is null)
                                            zskToRetire = new List<DnssecPrivateKey>();

                                        zskToRetire.Add(privateKey);
                                    }
                                    else
                                    {
                                        if (privateKey.IsRolloverNeeded())
                                        {
                                            if (zskToRollover is null)
                                                zskToRollover = new List<DnssecPrivateKey>();

                                            zskToRollover.Add(privateKey);
                                        }
                                    }
                                    break;

                                case DnssecPrivateKeyState.Retired:
                                    if (DateTime.UtcNow > privateKey.StateChangedOn.AddSeconds(dnsKeyTtl))
                                    {
                                        //key has been retired for sufficient time
                                        if (zskToDeactivateAndUnpublish is null)
                                            zskToDeactivateAndUnpublish = new List<DnssecPrivateKey>();

                                        zskToDeactivateAndUnpublish.Add(privateKey);
                                    }
                                    break;
                            }
                        }
                    }
                }

                //KSK actions
                if (kskToReady is not null)
                {
                    string dnsKeyTags = null;

                    foreach (DnssecPrivateKey kskPrivateKey in kskToReady)
                    {
                        kskPrivateKey.SetState(DnssecPrivateKeyState.Ready);

                        if (dnsKeyTags is null)
                            dnsKeyTags = kskPrivateKey.KeyTag.ToString();
                        else
                            dnsKeyTags += ", " + kskPrivateKey.KeyTag.ToString();
                    }

                    saveZone = true;

                    LogManager log = _dnsServer.LogManager;
                    if (log is not null)
                        log.Write("The KSK DNSKEYs (" + dnsKeyTags + ") from the primary zone are ready for changing the DS records at the parent zone: " + _name);
                }

                if (kskToActivate is not null)
                {
                    try
                    {
                        IReadOnlyList<DnssecPrivateKey> kskPrivateKeys = await GetDSPublishedPrivateKeys(kskToActivate);
                        if (kskPrivateKeys.Count > 0)
                        {
                            string dnsKeyTags = null;

                            foreach (DnssecPrivateKey kskPrivateKey in kskPrivateKeys)
                            {
                                kskPrivateKey.SetState(DnssecPrivateKeyState.Active);

                                if (dnsKeyTags is null)
                                    dnsKeyTags = kskPrivateKey.KeyTag.ToString();
                                else
                                    dnsKeyTags += ", " + kskPrivateKey.KeyTag.ToString();
                            }

                            saveZone = true;

                            LogManager log = _dnsServer.LogManager;
                            if (log is not null)
                                log.Write("The KSK DNSKEYs (" + dnsKeyTags + ") from the primary zone were activated successfully: " + _name);
                        }
                    }
                    catch (Exception ex)
                    {
                        LogManager log = _dnsServer.LogManager;
                        if (log is not null)
                            log.Write(ex);
                    }
                }

                if (kskToRetire is not null)
                {
                    string dnsKeyTags = null;

                    foreach (DnssecPrivateKey kskPrivateKey in kskToRetire)
                    {
                        bool isSafeToRetire = false;

                        lock (_dnssecPrivateKeys)
                        {
                            foreach (KeyValuePair<ushort, DnssecPrivateKey> privateKeyEntry in _dnssecPrivateKeys)
                            {
                                DnssecPrivateKey privateKey = privateKeyEntry.Value;

                                if ((privateKey.KeyType == DnssecPrivateKeyType.KeySigningKey) && (privateKey.Algorithm == kskPrivateKey.Algorithm) && !privateKey.IsRetiring)
                                {
                                    if (privateKey.State == DnssecPrivateKeyState.Active)
                                    {
                                        isSafeToRetire = true;
                                        break;
                                    }

                                    if ((privateKey.State == DnssecPrivateKeyState.Ready) && (kskPrivateKey.State == DnssecPrivateKeyState.Ready))
                                    {
                                        isSafeToRetire = true;
                                        break;
                                    }
                                }
                            }
                        }

                        if (isSafeToRetire)
                        {
                            kskPrivateKey.SetState(DnssecPrivateKeyState.Retired);
                            saveZone = true;

                            if (dnsKeyTags is null)
                                dnsKeyTags = kskPrivateKey.KeyTag.ToString();
                            else
                                dnsKeyTags += ", " + kskPrivateKey.KeyTag.ToString();
                        }
                    }

                    if (dnsKeyTags is not null)
                    {
                        LogManager log = _dnsServer.LogManager;
                        if (log is not null)
                            log.Write("The KSK DNSKEYs (" + dnsKeyTags + ") from the primary zone were retired successfully: " + _name);
                    }
                }

                if (kskToRevoke is not null)
                {
                    RevokeKskDnsKeys(kskToRevoke);
                    saveZone = true;
                }

                if (kskToUnpublish is not null)
                {
                    UnpublishDnsKeys(kskToUnpublish);
                    saveZone = true;
                }

                //ZSK actions
                if (zskToActivate is not null)
                {
                    ActivateZskDnsKeys(zskToActivate);
                    saveZone = true;
                }

                if (zskToRetire is not null)
                {
                    string dnsKeyTags = null;

                    foreach (DnssecPrivateKey zskPrivateKey in zskToRetire)
                    {
                        bool isSafeToRetire = false;

                        lock (_dnssecPrivateKeys)
                        {
                            foreach (KeyValuePair<ushort, DnssecPrivateKey> privateKeyEntry in _dnssecPrivateKeys)
                            {
                                DnssecPrivateKey privateKey = privateKeyEntry.Value;

                                if ((privateKey.KeyType == DnssecPrivateKeyType.ZoneSigningKey) && (privateKey.Algorithm == zskPrivateKey.Algorithm) && (privateKey.State == DnssecPrivateKeyState.Active) && !privateKey.IsRetiring)
                                {
                                    isSafeToRetire = true;
                                    break;
                                }
                            }
                        }

                        if (isSafeToRetire)
                        {
                            zskPrivateKey.SetState(DnssecPrivateKeyState.Retired);
                            saveZone = true;

                            if (dnsKeyTags is null)
                                dnsKeyTags = zskPrivateKey.KeyTag.ToString();
                            else
                                dnsKeyTags += ", " + zskPrivateKey.KeyTag.ToString();
                        }
                    }

                    if (dnsKeyTags is not null)
                    {
                        LogManager log = _dnsServer.LogManager;
                        if (log is not null)
                            log.Write("The ZSK DNSKEYs (" + dnsKeyTags + ") from the primary zone were retired successfully: " + _name);
                    }
                }

                if (zskToRollover is not null)
                {
                    foreach (DnssecPrivateKey zskPrivateKey in zskToRollover)
                        RolloverDnsKey(zskPrivateKey.KeyTag);

                    saveZone = true;
                }

                if (zskToDeactivateAndUnpublish is not null)
                {
                    DeactivateZskDnsKeys(zskToDeactivateAndUnpublish);
                    UnpublishDnsKeys(zskToDeactivateAndUnpublish);
                    saveZone = true;
                }

                //re-signing task
                uint reSignPeriod = GetSignatureValidityPeriod() / 10; //the period when signature refresh check is done
                if (DateTime.UtcNow > _lastSignatureRefreshCheckedOn.AddSeconds(reSignPeriod))
                {
                    if (TryRefreshAllSignatures())
                        saveZone = true;

                    _lastSignatureRefreshCheckedOn = DateTime.UtcNow;
                }

                if (saveZone)
                    _dnsServer.AuthZoneManager.SaveZoneFile(_name);
            }
            catch (Exception ex)
            {
                LogManager log = _dnsServer.LogManager;
                if (log is not null)
                    log.Write(ex);
            }
            finally
            {
                Timer dnssecTimer = _dnssecTimer;
                if (dnssecTimer is not null)
                {
                    lock (dnssecTimer)
                    {
                        dnssecTimer.Change(DNSSEC_TIMER_PERIODIC_INTERVAL, Timeout.Infinite);
                    }
                }
            }
        }

        public void SignZoneWithRsaNSec(string hashAlgorithm, int kskKeySize, int zskKeySize, uint dnsKeyTtl, ushort zskRolloverDays)
        {
            SignZoneWithRsa(hashAlgorithm, kskKeySize, zskKeySize, false, 0, 0, dnsKeyTtl, zskRolloverDays);
        }

        public void SignZoneWithRsaNSec3(string hashAlgorithm, int kskKeySize, int zskKeySize, ushort iterations, byte saltLength, uint dnsKeyTtl, ushort zskRolloverDays)
        {
            SignZoneWithRsa(hashAlgorithm, kskKeySize, zskKeySize, true, iterations, saltLength, dnsKeyTtl, zskRolloverDays);
        }

        private void SignZoneWithRsa(string hashAlgorithm, int kskKeySize, int zskKeySize, bool useNSec3, ushort iterations, byte saltLength, uint dnsKeyTtl, ushort zskRolloverDays)
        {
            if (_dnssecStatus != AuthZoneDnssecStatus.Unsigned)
                throw new DnsServerException("Cannot sign zone: the zone is already signed.");

            if (iterations > 50)
                throw new ArgumentOutOfRangeException(nameof(iterations), "NSEC3 iterations valid range is 0-50");

            if (saltLength > 32)
                throw new ArgumentOutOfRangeException(nameof(saltLength), "NSEC3 salt length valid range is 0-32");

            //generate private keys
            DnssecAlgorithm algorithm;

            switch (hashAlgorithm.ToUpper())
            {
                case "MD5":
                    algorithm = DnssecAlgorithm.RSAMD5;
                    break;

                case "SHA1":
                    algorithm = DnssecAlgorithm.RSASHA1;
                    break;

                case "SHA256":
                    algorithm = DnssecAlgorithm.RSASHA256;
                    break;

                case "SHA512":
                    algorithm = DnssecAlgorithm.RSASHA512;
                    break;

                default:
                    throw new NotSupportedException("Hash algorithm is not supported: " + hashAlgorithm);
            }

            DnssecPrivateKey kskPrivateKey = DnssecPrivateKey.Create(algorithm, DnssecPrivateKeyType.KeySigningKey, kskKeySize);
            DnssecPrivateKey zskPrivateKey = DnssecPrivateKey.Create(algorithm, DnssecPrivateKeyType.ZoneSigningKey, zskKeySize);

            zskPrivateKey.RolloverDays = zskRolloverDays;

            _dnssecPrivateKeys = new Dictionary<ushort, DnssecPrivateKey>(4);

            _dnssecPrivateKeys.Add(kskPrivateKey.KeyTag, kskPrivateKey);
            _dnssecPrivateKeys.Add(zskPrivateKey.KeyTag, zskPrivateKey);

            //sign zone
            SignZone(useNSec3, iterations, saltLength, dnsKeyTtl);
        }

        public void SignZoneWithEcdsaNSec(string curve, uint dnsKeyTtl, ushort zskRolloverDays)
        {
            SignZoneWithEcdsa(curve, false, 0, 0, dnsKeyTtl, zskRolloverDays);
        }

        public void SignZoneWithEcdsaNSec3(string curve, ushort iterations, byte saltLength, uint dnsKeyTtl, ushort zskRolloverDays)
        {
            SignZoneWithEcdsa(curve, true, iterations, saltLength, dnsKeyTtl, zskRolloverDays);
        }

        private void SignZoneWithEcdsa(string curve, bool useNSec3, ushort iterations, byte saltLength, uint dnsKeyTtl, ushort zskRolloverDays)
        {
            if (_dnssecStatus != AuthZoneDnssecStatus.Unsigned)
                throw new DnsServerException("Cannot sign zone: the zone is already signed.");

            if (iterations > 50)
                throw new ArgumentOutOfRangeException(nameof(iterations), "NSEC3 iterations valid range is 0-50");

            if (saltLength > 32)
                throw new ArgumentOutOfRangeException(nameof(saltLength), "NSEC3 salt length valid range is 0-32");

            //generate private keys
            DnssecAlgorithm algorithm;

            switch (curve.ToUpper())
            {
                case "P256":
                    algorithm = DnssecAlgorithm.ECDSAP256SHA256;
                    break;

                case "P384":
                    algorithm = DnssecAlgorithm.ECDSAP384SHA384;
                    break;

                default:
                    throw new NotSupportedException("ECDSA curve is not supported: " + curve);
            }

            DnssecPrivateKey kskPrivateKey = DnssecPrivateKey.Create(algorithm, DnssecPrivateKeyType.KeySigningKey);
            DnssecPrivateKey zskPrivateKey = DnssecPrivateKey.Create(algorithm, DnssecPrivateKeyType.ZoneSigningKey);

            zskPrivateKey.RolloverDays = zskRolloverDays;

            _dnssecPrivateKeys = new Dictionary<ushort, DnssecPrivateKey>(4);

            _dnssecPrivateKeys.Add(kskPrivateKey.KeyTag, kskPrivateKey);
            _dnssecPrivateKeys.Add(zskPrivateKey.KeyTag, zskPrivateKey);

            //sign zone
            SignZone(useNSec3, iterations, saltLength, dnsKeyTtl);
        }

        private void SignZone(bool useNSec3, ushort iterations, byte saltLength, uint dnsKeyTtl)
        {
            try
            {
                //update private key state
                foreach (KeyValuePair<ushort, DnssecPrivateKey> privateKeyEntry in _dnssecPrivateKeys)
                {
                    DnssecPrivateKey privateKey = privateKeyEntry.Value;

                    switch (privateKey.KeyType)
                    {
                        case DnssecPrivateKeyType.KeySigningKey:
                            privateKey.SetState(DnssecPrivateKeyState.Ready);
                            break;

                        case DnssecPrivateKeyType.ZoneSigningKey:
                            privateKey.SetState(DnssecPrivateKeyState.Ready);
                            break;
                    }
                }

                List<DnsResourceRecord> addedRecords = new List<DnsResourceRecord>();
                List<DnsResourceRecord> deletedRecords = new List<DnsResourceRecord>();

                //add DNSKEYs
                List<DnsResourceRecord> dnsKeyRecords = new List<DnsResourceRecord>(_dnssecPrivateKeys.Count);

                foreach (KeyValuePair<ushort, DnssecPrivateKey> privateKey in _dnssecPrivateKeys)
                    dnsKeyRecords.Add(new DnsResourceRecord(_name, DnsResourceRecordType.DNSKEY, DnsClass.IN, dnsKeyTtl, privateKey.Value.DnsKey));

                if (!TrySetRecords(DnsResourceRecordType.DNSKEY, dnsKeyRecords, out IReadOnlyList<DnsResourceRecord> deletedDnsKeyRecords))
                    throw new InvalidOperationException("Failed to add DNSKEY.");

                addedRecords.AddRange(dnsKeyRecords);
                deletedRecords.AddRange(deletedDnsKeyRecords);

                //sign all RRSets
                IReadOnlyList<AuthZone> zones = _dnsServer.AuthZoneManager.GetZoneWithSubDomainZones(_name);

                foreach (AuthZone zone in zones)
                {
                    IReadOnlyList<DnsResourceRecord> newRRSigRecords = zone.SignAllRRSets();
                    if (newRRSigRecords.Count > 0)
                    {
                        zone.AddOrUpdateRRSigRecords(newRRSigRecords, out IReadOnlyList<DnsResourceRecord> deletedRRSigRecords);

                        addedRecords.AddRange(newRRSigRecords);
                        deletedRecords.AddRange(deletedRRSigRecords);
                    }
                }

                if (useNSec3)
                {
                    EnableNSec3(zones, iterations, saltLength);
                    _dnssecStatus = AuthZoneDnssecStatus.SignedWithNSEC3;
                }
                else
                {
                    EnableNSec(zones);
                    _dnssecStatus = AuthZoneDnssecStatus.SignedWithNSEC;
                }

                //update private key state
                foreach (KeyValuePair<ushort, DnssecPrivateKey> privateKeyEntry in _dnssecPrivateKeys)
                {
                    DnssecPrivateKey privateKey = privateKeyEntry.Value;

                    switch (privateKey.KeyType)
                    {
                        case DnssecPrivateKeyType.ZoneSigningKey:
                            privateKey.SetState(DnssecPrivateKeyState.Active);
                            break;
                    }
                }

                _dnssecTimer = new Timer(DnssecTimerCallback, null, DNSSEC_TIMER_INITIAL_INTERVAL, Timeout.Infinite);

                CommitAndIncrementSerial(deletedRecords, addedRecords);
                TriggerNotify();
            }
            catch
            {
                _dnssecStatus = AuthZoneDnssecStatus.Unsigned;
                _dnssecPrivateKeys = null;

                throw;
            }
        }

        public void UnsignZone()
        {
            if (_dnssecStatus == AuthZoneDnssecStatus.Unsigned)
                throw new DnsServerException("Cannot unsign zone: the is zone not signed.");

            List<DnsResourceRecord> deletedRecords = new List<DnsResourceRecord>();
            IReadOnlyList<AuthZone> zones = _dnsServer.AuthZoneManager.GetZoneWithSubDomainZones(_name);

            foreach (AuthZone zone in zones)
            {
                deletedRecords.AddRange(zone.RemoveAllDnssecRecords());

                if (zone is SubDomainZone subDomainZone)
                {
                    if (zone.IsEmpty)
                        _dnsServer.AuthZoneManager.RemoveSubDomainZone(zone.Name); //remove empty sub zone
                    else
                        subDomainZone.AutoUpdateState();
                }
            }

            Timer dnssecTimer = _dnssecTimer;
            if (dnssecTimer is not null)
            {
                lock (dnssecTimer)
                {
                    dnssecTimer.Dispose();
                    _dnssecTimer = null;
                }
            }

            _dnssecPrivateKeys = null;
            _dnssecStatus = AuthZoneDnssecStatus.Unsigned;

            CommitAndIncrementSerial(deletedRecords);
            TriggerNotify();
        }

        public void ConvertToNSec()
        {
            if (_dnssecStatus != AuthZoneDnssecStatus.SignedWithNSEC3)
                throw new DnsServerException("Cannot convert to NSEC: the zone must be signed with NSEC3 for conversion.");

            lock (_dnssecUpdateLock)
            {
                IReadOnlyList<AuthZone> zones = _dnsServer.AuthZoneManager.GetZoneWithSubDomainZones(_name);

                DisableNSec3(zones);

                //since zones were removed when disabling NSEC3; get updated non empty zones list
                List<AuthZone> nonEmptyZones = new List<AuthZone>(zones.Count);

                foreach (AuthZone zone in zones)
                {
                    if (!zone.IsEmpty)
                        nonEmptyZones.Add(zone);
                }

                EnableNSec(nonEmptyZones);

                _dnssecStatus = AuthZoneDnssecStatus.SignedWithNSEC;
            }

            TriggerNotify();
        }

        public void ConvertToNSec3(ushort iterations, byte saltLength)
        {
            if (_dnssecStatus != AuthZoneDnssecStatus.SignedWithNSEC)
                throw new DnsServerException("Cannot convert to NSEC3: the zone must be signed with NSEC for conversion.");

            if (iterations > 50)
                throw new ArgumentOutOfRangeException(nameof(iterations), "NSEC3 iterations valid range is 0-50");

            if (saltLength > 32)
                throw new ArgumentOutOfRangeException(nameof(saltLength), "NSEC3 salt length valid range is 0-32");

            lock (_dnssecUpdateLock)
            {
                IReadOnlyList<AuthZone> zones = _dnsServer.AuthZoneManager.GetZoneWithSubDomainZones(_name);

                DisableNSec(zones);
                EnableNSec3(zones, iterations, saltLength);

                _dnssecStatus = AuthZoneDnssecStatus.SignedWithNSEC3;
            }

            TriggerNotify();
        }

        public void UpdateNSec3Parameters(ushort iterations, byte saltLength)
        {
            if (_dnssecStatus != AuthZoneDnssecStatus.SignedWithNSEC3)
                throw new DnsServerException("Cannot update NSEC3 parameters: the zone must be signed with NSEC3 first.");

            if (iterations > 50)
                throw new ArgumentOutOfRangeException(nameof(iterations), "NSEC3 iterations valid range is 0-50");

            if (saltLength > 32)
                throw new ArgumentOutOfRangeException(nameof(saltLength), "NSEC3 salt length valid range is 0-32");

            lock (_dnssecUpdateLock)
            {
                IReadOnlyList<AuthZone> zones = _dnsServer.AuthZoneManager.GetZoneWithSubDomainZones(_name);

                DisableNSec3(zones);

                //since zones were removed when disabling NSEC3; get updated non empty zones list
                List<AuthZone> nonEmptyZones = new List<AuthZone>(zones.Count);

                foreach (AuthZone zone in zones)
                {
                    if (!zone.IsEmpty)
                        nonEmptyZones.Add(zone);
                }

                EnableNSec3(nonEmptyZones, iterations, saltLength);
            }

            TriggerNotify();
        }

        private void RefreshNSec()
        {
            lock (_dnssecUpdateLock)
            {
                IReadOnlyList<AuthZone> zones = _dnsServer.AuthZoneManager.GetZoneWithSubDomainZones(_name);

                EnableNSec(zones);
            }
        }

        private void RefreshNSec3()
        {
            lock (_dnssecUpdateLock)
            {
                IReadOnlyList<AuthZone> zones = _dnsServer.AuthZoneManager.GetZoneWithSubDomainZones(_name);

                //get non NSEC3 zones
                List<AuthZone> nonNSec3Zones = new List<AuthZone>(zones.Count);

                foreach (AuthZone zone in zones)
                {
                    if (zone.HasOnlyNSec3Records())
                        continue;

                    nonNSec3Zones.Add(zone);
                }

                IReadOnlyList<DnsResourceRecord> nsec3ParamRecords = GetRecords(DnsResourceRecordType.NSEC3PARAM);
                DnsNSEC3PARAMRecordData nsec3Param = nsec3ParamRecords[0].RDATA as DnsNSEC3PARAMRecordData;

                EnableNSec3(nonNSec3Zones, nsec3Param.Iterations, nsec3Param.SaltValue);
            }
        }

        private void EnableNSec(IReadOnlyList<AuthZone> zones)
        {
            List<DnsResourceRecord> addedRecords = new List<DnsResourceRecord>();
            List<DnsResourceRecord> deletedRecords = new List<DnsResourceRecord>();

            uint ttl = GetZoneSoaMinimum();

            for (int i = 0; i < zones.Count; i++)
            {
                AuthZone zone = zones[i];
                AuthZone nextZone;

                if (i < zones.Count - 1)
                    nextZone = zones[i + 1];
                else
                    nextZone = zones[0];

                IReadOnlyList<DnsResourceRecord> newNSecRecords = zone.GetUpdatedNSecRRSet(nextZone.Name, ttl);
                if (newNSecRecords.Count > 0)
                {
                    if (!zone.TrySetRecords(DnsResourceRecordType.NSEC, newNSecRecords, out IReadOnlyList<DnsResourceRecord> deletedNSecRecords))
                        throw new DnsServerException("Failed to set DNSSEC records. Please try again.");

                    addedRecords.AddRange(newNSecRecords);
                    deletedRecords.AddRange(deletedNSecRecords);

                    IReadOnlyList<DnsResourceRecord> newRRSigRecords = SignRRSet(newNSecRecords);
                    if (newRRSigRecords.Count > 0)
                    {
                        zone.AddOrUpdateRRSigRecords(newRRSigRecords, out IReadOnlyList<DnsResourceRecord> deletedRRSigRecords);

                        addedRecords.AddRange(newRRSigRecords);
                        deletedRecords.AddRange(deletedRRSigRecords);
                    }
                }
            }

            CommitAndIncrementSerial(deletedRecords, addedRecords);
        }

        private void DisableNSec(IReadOnlyList<AuthZone> zones)
        {
            List<DnsResourceRecord> deletedRecords = new List<DnsResourceRecord>();

            foreach (AuthZone zone in zones)
                deletedRecords.AddRange(zone.RemoveNSecRecordsWithRRSig());

            CommitAndIncrementSerial(deletedRecords);
        }

        private void EnableNSec3(IReadOnlyList<AuthZone> zones, ushort iterations, byte saltLength)
        {
            byte[] salt;

            if (saltLength > 0)
            {
                salt = new byte[saltLength];
                using RandomNumberGenerator rng = RandomNumberGenerator.Create();
                rng.GetBytes(salt);
            }
            else
            {
                salt = Array.Empty<byte>();
            }

            EnableNSec3(zones, iterations, salt);
        }

        private void EnableNSec3(IReadOnlyList<AuthZone> zones, ushort iterations, byte[] salt)
        {
            List<DnsResourceRecord> addedRecords = new List<DnsResourceRecord>();
            List<DnsResourceRecord> deletedRecords = new List<DnsResourceRecord>();

            List<DnsResourceRecord> partialNSec3Records = new List<DnsResourceRecord>(zones.Count);
            int apexLabelCount = DnsRRSIGRecordData.GetLabelCount(_name);

            uint ttl = GetZoneSoaMinimum();

            //list all partial NSEC3 records
            foreach (AuthZone zone in zones)
            {
                partialNSec3Records.Add(zone.GetPartialNSec3Record(_name, ttl, iterations, salt));

                int zoneLabelCount = DnsRRSIGRecordData.GetLabelCount(zone.Name);
                if ((zoneLabelCount - apexLabelCount) > 1)
                {
                    //empty non-terminal (ENT) may exists
                    string currentOwnerName = zone.Name;

                    while (true)
                    {
                        currentOwnerName = AuthZoneManager.GetParentZone(currentOwnerName);
                        if (currentOwnerName.Equals(_name, StringComparison.OrdinalIgnoreCase))
                            break;

                        //add partial NSEC3 record for ENT
                        AuthZone entZone = new PrimarySubDomainZone(null, currentOwnerName); //dummy empty non-terminal (ENT) sub domain object
                        partialNSec3Records.Add(entZone.GetPartialNSec3Record(_name, ttl, iterations, salt));
                    }
                }
            }

            //sort partial NSEC3 records
            partialNSec3Records.Sort(delegate (DnsResourceRecord rr1, DnsResourceRecord rr2)
            {
                return string.CompareOrdinal(rr1.Name, rr2.Name);
            });

            //deduplicate partial NSEC3 records and insert next hashed owner name to complete them
            List<DnsResourceRecord> uniqueNSec3Records = new List<DnsResourceRecord>(partialNSec3Records.Count);

            for (int i = 0; i < partialNSec3Records.Count; i++)
            {
                DnsResourceRecord partialNSec3Record = partialNSec3Records[i];
                DnsResourceRecord nextPartialNSec3Record;

                if (i < partialNSec3Records.Count - 1)
                {
                    nextPartialNSec3Record = partialNSec3Records[i + 1];

                    //check for duplicates
                    if (partialNSec3Record.Name.Equals(nextPartialNSec3Record.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        //found duplicate; merge current nsec3 into next nsec3
                        DnsNSEC3RecordData nsec3 = partialNSec3Record.RDATA as DnsNSEC3RecordData;
                        DnsNSEC3RecordData nextNSec3 = nextPartialNSec3Record.RDATA as DnsNSEC3RecordData;

                        List<DnsResourceRecordType> uniqueTypes = new List<DnsResourceRecordType>(nsec3.Types.Count + nextNSec3.Types.Count);
                        uniqueTypes.AddRange(nsec3.Types);

                        foreach (DnsResourceRecordType type in nextNSec3.Types)
                        {
                            if (!uniqueTypes.Contains(type))
                                uniqueTypes.Add(type);
                        }

                        uniqueTypes.Sort();

                        //update the next nsec3 record and continue
                        DnsNSEC3RecordData mergedPartialNSec3 = new DnsNSEC3RecordData(DnssecNSEC3HashAlgorithm.SHA1, DnssecNSEC3Flags.None, iterations, salt, Array.Empty<byte>(), uniqueTypes);
                        partialNSec3Records[i + 1] = new DnsResourceRecord(partialNSec3Record.Name, DnsResourceRecordType.NSEC3, DnsClass.IN, ttl, mergedPartialNSec3);
                        continue;
                    }
                }
                else
                {
                    //for last NSEC3, next NSEC3 is the first in list
                    nextPartialNSec3Record = partialNSec3Records[0];
                }

                //add NSEC3 record with next hashed owner name
                {
                    DnsNSEC3RecordData partialNSec3 = partialNSec3Record.RDATA as DnsNSEC3RecordData;
                    byte[] nextHashedOwnerName = DnsNSEC3RecordData.GetHashedOwnerNameFrom(nextPartialNSec3Record.Name);

                    DnsNSEC3RecordData updatedNSec3 = new DnsNSEC3RecordData(DnssecNSEC3HashAlgorithm.SHA1, DnssecNSEC3Flags.None, iterations, salt, nextHashedOwnerName, partialNSec3.Types);
                    uniqueNSec3Records.Add(new DnsResourceRecord(partialNSec3Record.Name, DnsResourceRecordType.NSEC3, DnsClass.IN, ttl, updatedNSec3));
                }
            }

            //insert and sign NSEC3 records
            foreach (DnsResourceRecord uniqueNSec3Record in uniqueNSec3Records)
            {
                AuthZone zone = _dnsServer.AuthZoneManager.GetOrAddSubDomainZone(_name, uniqueNSec3Record.Name);

                DnsResourceRecord[] newNSec3Records = new DnsResourceRecord[] { uniqueNSec3Record };

                if (!zone.TrySetRecords(DnsResourceRecordType.NSEC3, newNSec3Records, out IReadOnlyList<DnsResourceRecord> deletedNSec3Records))
                    throw new InvalidOperationException();

                addedRecords.AddRange(newNSec3Records);
                deletedRecords.AddRange(deletedNSec3Records);

                IReadOnlyList<DnsResourceRecord> newRRSigRecords = SignRRSet(newNSec3Records);
                if (newRRSigRecords.Count > 0)
                {
                    zone.AddOrUpdateRRSigRecords(newRRSigRecords, out IReadOnlyList<DnsResourceRecord> deletedRRSigRecords);

                    addedRecords.AddRange(newRRSigRecords);
                    deletedRecords.AddRange(deletedRRSigRecords);
                }
            }

            //insert and sign NSEC3PARAM record
            {
                DnsNSEC3PARAMRecordData newNSec3Param = new DnsNSEC3PARAMRecordData(DnssecNSEC3HashAlgorithm.SHA1, DnssecNSEC3Flags.None, iterations, salt);
                DnsResourceRecord[] newNSec3ParamRecords = new DnsResourceRecord[] { new DnsResourceRecord(_name, DnsResourceRecordType.NSEC3PARAM, DnsClass.IN, ttl, newNSec3Param) };

                if (!TrySetRecords(DnsResourceRecordType.NSEC3PARAM, newNSec3ParamRecords, out IReadOnlyList<DnsResourceRecord> deletedNSec3ParamRecords))
                    throw new InvalidOperationException();

                addedRecords.AddRange(newNSec3ParamRecords);
                deletedRecords.AddRange(deletedNSec3ParamRecords);

                IReadOnlyList<DnsResourceRecord> newRRSigRecords = SignRRSet(newNSec3ParamRecords);
                if (newRRSigRecords.Count > 0)
                {
                    AddOrUpdateRRSigRecords(newRRSigRecords, out IReadOnlyList<DnsResourceRecord> deletedRRSigRecords);

                    addedRecords.AddRange(newRRSigRecords);
                    deletedRecords.AddRange(deletedRRSigRecords);
                }
            }

            CommitAndIncrementSerial(deletedRecords, addedRecords);
        }

        private void DisableNSec3(IReadOnlyList<AuthZone> zones)
        {
            List<DnsResourceRecord> deletedRecords = new List<DnsResourceRecord>();

            foreach (AuthZone zone in zones)
            {
                deletedRecords.AddRange(zone.RemoveNSec3RecordsWithRRSig());

                if (zone is SubDomainZone subDomainZone)
                {
                    if (zone.IsEmpty)
                        _dnsServer.AuthZoneManager.RemoveSubDomainZone(zone.Name); //remove empty sub zone
                    else
                        subDomainZone.AutoUpdateState();
                }
            }

            CommitAndIncrementSerial(deletedRecords);
        }

        public void GenerateAndAddRsaKey(DnssecPrivateKeyType keyType, string hashAlgorithm, int keySize, ushort rolloverDays)
        {
            if (_dnssecStatus == AuthZoneDnssecStatus.Unsigned)
                throw new DnsServerException("The zone must be signed.");

            DnssecAlgorithm algorithm;

            switch (hashAlgorithm.ToUpper())
            {
                case "MD5":
                    algorithm = DnssecAlgorithm.RSAMD5;
                    break;

                case "SHA1":
                    algorithm = DnssecAlgorithm.RSASHA1;
                    break;

                case "SHA256":
                    algorithm = DnssecAlgorithm.RSASHA256;
                    break;

                case "SHA512":
                    algorithm = DnssecAlgorithm.RSASHA512;
                    break;

                default:
                    throw new NotSupportedException("Hash algorithm is not supported: " + hashAlgorithm);
            }

            GenerateAndAddRsaKey(keyType, algorithm, keySize, rolloverDays);
        }

        private void GenerateAndAddRsaKey(DnssecPrivateKeyType keyType, DnssecAlgorithm algorithm, int keySize, ushort rolloverDays)
        {
            int i = 0;
            while (i++ < 5)
            {
                DnssecPrivateKey privateKey = DnssecPrivateKey.Create(algorithm, keyType, keySize);
                privateKey.RolloverDays = rolloverDays;

                lock (_dnssecPrivateKeys)
                {
                    if (_dnssecPrivateKeys.TryAdd(privateKey.KeyTag, privateKey))
                        return;
                }
            }

            throw new DnsServerException("Failed to add private key: key tag collision.");
        }

        public void GenerateAndAddEcdsaKey(DnssecPrivateKeyType keyType, string curve, ushort rolloverDays)
        {
            if (_dnssecStatus == AuthZoneDnssecStatus.Unsigned)
                throw new DnsServerException("The zone must be signed.");

            DnssecAlgorithm algorithm;

            switch (curve.ToUpper())
            {
                case "P256":
                    algorithm = DnssecAlgorithm.ECDSAP256SHA256;
                    break;

                case "P384":
                    algorithm = DnssecAlgorithm.ECDSAP384SHA384;
                    break;

                default:
                    throw new NotSupportedException("ECDSA curve is not supported: " + curve);
            }

            GenerateAndAddEcdsaKey(keyType, algorithm, rolloverDays);
        }

        private void GenerateAndAddEcdsaKey(DnssecPrivateKeyType keyType, DnssecAlgorithm algorithm, ushort rolloverDays)
        {
            int i = 0;
            while (i++ < 5)
            {
                DnssecPrivateKey privateKey = DnssecPrivateKey.Create(algorithm, keyType);
                privateKey.RolloverDays = rolloverDays;

                lock (_dnssecPrivateKeys)
                {
                    if (_dnssecPrivateKeys.TryAdd(privateKey.KeyTag, privateKey))
                        return;
                }
            }

            throw new DnsServerException("Failed to add private key: key tag collision.");
        }

        public void UpdatePrivateKey(ushort keyTag, ushort rolloverDays)
        {
            lock (_dnssecPrivateKeys)
            {
                if (!_dnssecPrivateKeys.TryGetValue(keyTag, out DnssecPrivateKey privateKey))
                    throw new DnsServerException("Cannot update private key: no such private key was found.");

                privateKey.RolloverDays = rolloverDays;
            }
        }

        public void DeletePrivateKey(ushort keyTag)
        {
            if (_dnssecStatus == AuthZoneDnssecStatus.Unsigned)
                throw new DnsServerException("The zone must be signed.");

            lock (_dnssecPrivateKeys)
            {
                if (!_dnssecPrivateKeys.TryGetValue(keyTag, out DnssecPrivateKey privateKey))
                    throw new DnsServerException("Cannot delete private key: no such private key was found.");

                if (privateKey.State != DnssecPrivateKeyState.Generated)
                    throw new DnsServerException("Cannot delete private key: only keys with Generated state can be deleted.");

                _dnssecPrivateKeys.Remove(keyTag);
            }
        }

        public void PublishAllGeneratedKeys()
        {
            if (_dnssecStatus == AuthZoneDnssecStatus.Unsigned)
                throw new DnsServerException("The zone must be signed.");

            List<DnssecPrivateKey> generatedPrivateKeys = new List<DnssecPrivateKey>();
            List<DnsResourceRecord> newDnsKeyRecords = new List<DnsResourceRecord>();

            uint dnsKeyTtl = GetDnsKeyTtl();

            lock (_dnssecPrivateKeys)
            {
                foreach (KeyValuePair<ushort, DnssecPrivateKey> privateKeyEntry in _dnssecPrivateKeys)
                {
                    DnssecPrivateKey privateKey = privateKeyEntry.Value;

                    if (privateKey.State == DnssecPrivateKeyState.Generated)
                    {
                        generatedPrivateKeys.Add(privateKey);
                        newDnsKeyRecords.Add(new DnsResourceRecord(_name, DnsResourceRecordType.DNSKEY, DnsClass.IN, dnsKeyTtl, privateKey.DnsKey));
                    }
                }
            }

            if (generatedPrivateKeys.Count == 0)
                throw new DnsServerException("Cannot publish DNSKEY: no generated private keys were found.");

            IReadOnlyList<DnsResourceRecord> dnsKeyRecords = _entries.AddOrUpdate(DnsResourceRecordType.DNSKEY, delegate (DnsResourceRecordType key)
            {
                return newDnsKeyRecords;
            },
            delegate (DnsResourceRecordType key, IReadOnlyList<DnsResourceRecord> existingRecords)
            {
                foreach (DnsResourceRecord existingRecord in existingRecords)
                {
                    foreach (DnsResourceRecord newDnsKeyRecord in newDnsKeyRecords)
                    {
                        if (existingRecord.Equals(newDnsKeyRecord))
                            throw new DnsServerException("Cannot publish DNSKEY: the key is already published.");
                    }
                }

                List<DnsResourceRecord> dnsKeyRecords = new List<DnsResourceRecord>(existingRecords.Count + newDnsKeyRecords.Count);

                dnsKeyRecords.AddRange(existingRecords);
                dnsKeyRecords.AddRange(newDnsKeyRecords);

                return dnsKeyRecords;
            });

            List<DnsResourceRecord> addedRecords = new List<DnsResourceRecord>();
            List<DnsResourceRecord> deletedRecords = new List<DnsResourceRecord>();

            addedRecords.AddRange(newDnsKeyRecords);

            IReadOnlyList<DnsResourceRecord> newRRSigRecords = SignRRSet(dnsKeyRecords);
            if (newRRSigRecords.Count > 0)
            {
                AddOrUpdateRRSigRecords(newRRSigRecords, out IReadOnlyList<DnsResourceRecord> deletedRRSigRecords);

                addedRecords.AddRange(newRRSigRecords);
                deletedRecords.AddRange(deletedRRSigRecords);
            }

            CommitAndIncrementSerial(deletedRecords, addedRecords);
            TriggerNotify();

            //update private key state
            foreach (DnssecPrivateKey privateKey in generatedPrivateKeys)
                privateKey.SetState(DnssecPrivateKeyState.Published);
        }

        private void ActivateZskDnsKeys(IReadOnlyList<DnssecPrivateKey> zskPrivateKeys)
        {
            List<DnsResourceRecord> addedRecords = new List<DnsResourceRecord>();
            List<DnsResourceRecord> deletedRecords = new List<DnsResourceRecord>();

            //re-sign all records with new private keys
            IReadOnlyList<AuthZone> zones = _dnsServer.AuthZoneManager.GetZoneWithSubDomainZones(_name);

            foreach (AuthZone zone in zones)
            {
                IReadOnlyList<DnsResourceRecord> newRRSigRecords = zone.SignAllRRSets();
                if (newRRSigRecords.Count > 0)
                {
                    zone.AddOrUpdateRRSigRecords(newRRSigRecords, out IReadOnlyList<DnsResourceRecord> deletedRRSigRecords);

                    addedRecords.AddRange(newRRSigRecords);
                    deletedRecords.AddRange(deletedRRSigRecords);
                }
            }

            CommitAndIncrementSerial(deletedRecords, addedRecords);
            TriggerNotify();

            //update private key state
            string dnsKeyTags = null;

            foreach (DnssecPrivateKey privateKey in zskPrivateKeys)
            {
                privateKey.SetState(DnssecPrivateKeyState.Active);

                if (dnsKeyTags is null)
                    dnsKeyTags = privateKey.KeyTag.ToString();
                else
                    dnsKeyTags += ", " + privateKey.KeyTag.ToString();
            }

            LogManager log = _dnsServer.LogManager;
            if (log is not null)
                log.Write("The ZSK DNSKEYs (" + dnsKeyTags + ") from the primary zone were activated successfully: " + _name);
        }

        public void RolloverDnsKey(ushort keyTag)
        {
            if (_dnssecStatus == AuthZoneDnssecStatus.Unsigned)
                throw new DnsServerException("The zone must be signed.");

            DnssecPrivateKey privateKey;

            lock (_dnssecPrivateKeys)
            {
                if (!_dnssecPrivateKeys.TryGetValue(keyTag, out privateKey))
                    throw new DnsServerException("Cannot rollover private key: no such private key was found.");
            }

            switch (privateKey.State)
            {
                case DnssecPrivateKeyState.Ready:
                case DnssecPrivateKeyState.Active:
                    if (privateKey.IsRetiring)
                        throw new DnsServerException("Cannot rollover private key: the private key is already set to retire.");

                    break;

                default:
                    throw new DnsServerException("Cannot rollover private key: the private key state must be Ready or Active to be able to rollover.");
            }

            switch (privateKey.Algorithm)
            {
                case DnssecAlgorithm.RSAMD5:
                case DnssecAlgorithm.RSASHA1:
                case DnssecAlgorithm.RSASHA1_NSEC3_SHA1:
                case DnssecAlgorithm.RSASHA256:
                case DnssecAlgorithm.RSASHA512:
                    GenerateAndAddRsaKey(privateKey.KeyType, privateKey.Algorithm, (privateKey as DnssecRsaPrivateKey).KeySize, privateKey.RolloverDays);
                    break;

                case DnssecAlgorithm.ECDSAP256SHA256:
                case DnssecAlgorithm.ECDSAP384SHA384:
                    GenerateAndAddEcdsaKey(privateKey.KeyType, privateKey.Algorithm, privateKey.RolloverDays);
                    break;

                default:
                    throw new NotSupportedException("DNSSEC algorithm is not supported: " + privateKey.Algorithm.ToString());
            }

            PublishAllGeneratedKeys();
            privateKey.SetToRetire();
        }

        public void RetireDnsKey(ushort keyTag)
        {
            if (_dnssecStatus == AuthZoneDnssecStatus.Unsigned)
                throw new DnsServerException("The zone must be signed.");

            DnssecPrivateKey privateKeyToRetire;

            lock (_dnssecPrivateKeys)
            {
                if (!_dnssecPrivateKeys.TryGetValue(keyTag, out privateKeyToRetire))
                    throw new DnsServerException("Cannot retire private key: no such private key was found.");
            }

            switch (privateKeyToRetire.State)
            {
                case DnssecPrivateKeyState.Ready:
                case DnssecPrivateKeyState.Active:
                    int readyCount = 0;
                    int activeCount = 0;

                    lock (_dnssecPrivateKeys)
                    {
                        foreach (KeyValuePair<ushort, DnssecPrivateKey> privateKeyEntry in _dnssecPrivateKeys)
                        {
                            if (privateKeyEntry.Key == privateKeyToRetire.KeyTag)
                                continue; //skip self

                            DnssecPrivateKey privateKey = privateKeyEntry.Value;
                            if (privateKey.KeyType != privateKeyToRetire.KeyType)
                                continue;

                            switch (privateKey.State)
                            {
                                case DnssecPrivateKeyState.Ready:
                                    if (!privateKey.IsRetiring)
                                        readyCount++;

                                    break;

                                case DnssecPrivateKeyState.Active:
                                    if (!privateKey.IsRetiring)
                                        activeCount++;

                                    break;
                            }
                        }
                    }

                    bool isSafeToRetire;

                    if (privateKeyToRetire.KeyType == DnssecPrivateKeyType.KeySigningKey)
                        isSafeToRetire = (activeCount > 0) || ((readyCount > 0) && (privateKeyToRetire.State == DnssecPrivateKeyState.Ready));
                    else
                        isSafeToRetire = activeCount > 0;

                    if (!isSafeToRetire)
                        throw new DnsServerException("Cannot retire private key: no successor key was found to safely retire the key.");

                    //update private key state
                    privateKeyToRetire.SetState(DnssecPrivateKeyState.Retired);
                    break;

                default:
                    throw new DnsServerException("Cannot retire private key: the private key state must be Ready or Active to be able to retire.");
            }
        }

        private void DeactivateZskDnsKeys(IReadOnlyList<DnssecPrivateKey> privateKeys)
        {
            //remove all RRSIGs for the DNSKEYs
            List<DnsResourceRecord> deletedRecords = new List<DnsResourceRecord>();

            IReadOnlyList<AuthZone> zones = _dnsServer.AuthZoneManager.GetZoneWithSubDomainZones(_name);

            foreach (AuthZone zone in zones)
            {
                IReadOnlyList<DnsResourceRecord> rrsigRecords = zone.GetRecords(DnsResourceRecordType.RRSIG);
                List<DnsResourceRecord> rrsigsToRemove = new List<DnsResourceRecord>();

                foreach (DnsResourceRecord rrsigRecord in rrsigRecords)
                {
                    DnsRRSIGRecordData rrsig = rrsigRecord.RDATA as DnsRRSIGRecordData;

                    foreach (DnssecPrivateKey privateKey in privateKeys)
                    {
                        if (rrsig.KeyTag == privateKey.KeyTag)
                        {
                            rrsigsToRemove.Add(rrsigRecord);
                            break;
                        }
                    }
                }

                if (zone.TryDeleteRecords(DnsResourceRecordType.RRSIG, rrsigsToRemove, out IReadOnlyList<DnsResourceRecord> deletedRRSigRecords))
                    deletedRecords.AddRange(deletedRRSigRecords);
            }

            CommitAndIncrementSerial(deletedRecords);
            TriggerNotify();

            //update private key state
            string dnsKeyTags = null;

            foreach (DnssecPrivateKey privateKey in privateKeys)
            {
                privateKey.SetState(DnssecPrivateKeyState.Removed);

                if (dnsKeyTags is null)
                    dnsKeyTags = privateKey.KeyTag.ToString();
                else
                    dnsKeyTags += ", " + privateKey.KeyTag.ToString();
            }

            LogManager log = _dnsServer.LogManager;
            if (log is not null)
                log.Write("The ZSK DNSKEYs (" + dnsKeyTags + ") from the primary zone were deactivated successfully: " + _name);
        }

        private void RevokeKskDnsKeys(IReadOnlyList<DnssecPrivateKey> privateKeys)
        {
            if (!_entries.TryGetValue(DnsResourceRecordType.DNSKEY, out IReadOnlyList<DnsResourceRecord> existingDnsKeyRecords))
                throw new InvalidOperationException();

            List<DnsResourceRecord> addedRecords = new List<DnsResourceRecord>();
            List<DnsResourceRecord> deletedRecords = new List<DnsResourceRecord>();

            List<DnsResourceRecord> dnsKeyRecords = new List<DnsResourceRecord>();

            foreach (DnsResourceRecord existingDnsKeyRecord in existingDnsKeyRecords)
            {
                bool found = false;

                foreach (DnssecPrivateKey privateKey in privateKeys)
                {
                    if (existingDnsKeyRecord.RDATA.Equals(privateKey.DnsKey))
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                    dnsKeyRecords.Add(existingDnsKeyRecord);
            }

            uint dnsKeyTtl = existingDnsKeyRecords[0].OriginalTtlValue;
            List<ushort> keyTagsToRemove = new List<ushort>(privateKeys.Count);

            foreach (DnssecPrivateKey privateKey in privateKeys)
            {
                keyTagsToRemove.Add(privateKey.KeyTag);
                privateKey.SetState(DnssecPrivateKeyState.Revoked);

                DnsResourceRecord revokedDnsKeyRecord = new DnsResourceRecord(_name, DnsResourceRecordType.DNSKEY, DnsClass.IN, dnsKeyTtl, privateKey.DnsKey);
                addedRecords.Add(revokedDnsKeyRecord);
                dnsKeyRecords.Add(revokedDnsKeyRecord);
            }

            if (TrySetRecords(DnsResourceRecordType.DNSKEY, dnsKeyRecords, out IReadOnlyList<DnsResourceRecord> deletedDnsKeyRecords))
                deletedRecords.AddRange(deletedDnsKeyRecords);

            IReadOnlyList<DnsResourceRecord> newRRSigRecords = SignRRSet(dnsKeyRecords);
            if (newRRSigRecords.Count > 0)
            {
                AddOrUpdateRRSigRecords(newRRSigRecords, out IReadOnlyList<DnsResourceRecord> deletedRRSigRecords);

                addedRecords.AddRange(newRRSigRecords);
                deletedRecords.AddRange(deletedRRSigRecords);
            }

            //remove RRSIG for removed keys
            {
                IReadOnlyList<DnsResourceRecord> rrsigRecords = GetRecords(DnsResourceRecordType.RRSIG);
                List<DnsResourceRecord> rrsigsToRemove = new List<DnsResourceRecord>();

                foreach (DnsResourceRecord rrsigRecord in rrsigRecords)
                {
                    DnsRRSIGRecordData rrsig = rrsigRecord.RDATA as DnsRRSIGRecordData;
                    if (rrsig.TypeCovered != DnsResourceRecordType.DNSKEY)
                        continue;

                    foreach (ushort keyTagToRemove in keyTagsToRemove)
                    {
                        if (rrsig.KeyTag == keyTagToRemove)
                        {
                            rrsigsToRemove.Add(rrsigRecord);
                            break;
                        }
                    }
                }

                if (TryDeleteRecords(DnsResourceRecordType.RRSIG, rrsigsToRemove, out IReadOnlyList<DnsResourceRecord> deletedRRSigRecords))
                    deletedRecords.AddRange(deletedRRSigRecords);
            }

            CommitAndIncrementSerial(deletedRecords, addedRecords);
            TriggerNotify();

            //update revoked private keys
            string dnsKeyTags = null;

            lock (_dnssecPrivateKeys)
            {
                //remove old entry
                foreach (ushort keyTag in keyTagsToRemove)
                {
                    if (_dnssecPrivateKeys.Remove(keyTag))
                    {
                        if (dnsKeyTags is null)
                            dnsKeyTags = keyTag.ToString();
                        else
                            dnsKeyTags += ", " + keyTag.ToString();
                    }
                }

                //add new entry
                foreach (DnssecPrivateKey privateKey in privateKeys)
                    _dnssecPrivateKeys.Add(privateKey.KeyTag, privateKey);
            }

            LogManager log = _dnsServer.LogManager;
            if (log is not null)
                log.Write("The KSK DNSKEYs (" + dnsKeyTags + ") from the primary zone were revoked successfully: " + _name);
        }

        private void UnpublishDnsKeys(IReadOnlyList<DnssecPrivateKey> privateKeys)
        {
            if (!_entries.TryGetValue(DnsResourceRecordType.DNSKEY, out IReadOnlyList<DnsResourceRecord> existingDnsKeyRecords))
                throw new InvalidOperationException();

            List<DnsResourceRecord> addedRecords = new List<DnsResourceRecord>();
            List<DnsResourceRecord> deletedRecords = new List<DnsResourceRecord>();

            List<DnsResourceRecord> dnsKeyRecords = new List<DnsResourceRecord>();

            foreach (DnsResourceRecord existingDnsKeyRecord in existingDnsKeyRecords)
            {
                bool found = false;

                foreach (DnssecPrivateKey privateKey in privateKeys)
                {
                    if (existingDnsKeyRecord.RDATA.Equals(privateKey.DnsKey))
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                    dnsKeyRecords.Add(existingDnsKeyRecord);
            }

            if (dnsKeyRecords.Count < 2)
                throw new InvalidOperationException();

            if (TrySetRecords(DnsResourceRecordType.DNSKEY, dnsKeyRecords, out IReadOnlyList<DnsResourceRecord> deletedDnsKeyRecords))
                deletedRecords.AddRange(deletedDnsKeyRecords);

            IReadOnlyList<DnsResourceRecord> newRRSigRecords = SignRRSet(dnsKeyRecords);
            if (newRRSigRecords.Count > 0)
            {
                AddOrUpdateRRSigRecords(newRRSigRecords, out IReadOnlyList<DnsResourceRecord> deletedRRSigRecords);

                addedRecords.AddRange(newRRSigRecords);
                deletedRecords.AddRange(deletedRRSigRecords);
            }

            //remove RRSig for revoked keys
            {
                IReadOnlyList<DnsResourceRecord> rrsigRecords = GetRecords(DnsResourceRecordType.RRSIG);
                List<DnsResourceRecord> rrsigsToRemove = new List<DnsResourceRecord>();

                foreach (DnsResourceRecord rrsigRecord in rrsigRecords)
                {
                    DnsRRSIGRecordData rrsig = rrsigRecord.RDATA as DnsRRSIGRecordData;
                    if (rrsig.TypeCovered != DnsResourceRecordType.DNSKEY)
                        continue;

                    foreach (DnssecPrivateKey privateKey in privateKeys)
                    {
                        if (rrsig.KeyTag == privateKey.KeyTag)
                        {
                            rrsigsToRemove.Add(rrsigRecord);
                            break;
                        }
                    }
                }

                if (TryDeleteRecords(DnsResourceRecordType.RRSIG, rrsigsToRemove, out IReadOnlyList<DnsResourceRecord> deletedRRSigRecords))
                    deletedRecords.AddRange(deletedRRSigRecords);
            }

            CommitAndIncrementSerial(deletedRecords, addedRecords);
            TriggerNotify();

            //remove private keys permanently
            string dnsKeyTags = null;

            lock (_dnssecPrivateKeys)
            {
                foreach (DnssecPrivateKey privateKey in privateKeys)
                {
                    if (_dnssecPrivateKeys.Remove(privateKey.KeyTag))
                    {
                        if (dnsKeyTags is null)
                            dnsKeyTags = privateKey.KeyTag.ToString();
                        else
                            dnsKeyTags += ", " + privateKey.KeyTag.ToString();
                    }
                }
            }

            LogManager log = _dnsServer.LogManager;
            if (log is not null)
                log.Write("The DNSKEYs (" + dnsKeyTags + ") from the primary zone were unpublished successfully: " + _name);
        }

        private async Task<IReadOnlyList<DnssecPrivateKey>> GetDSPublishedPrivateKeys(IReadOnlyList<DnssecPrivateKey> privateKeys)
        {
            if (_name.Length == 0)
                return privateKeys; //zone is root

            IReadOnlyList<DnsDSRecordData> dsRecords = DnsClient.ParseResponseDS(await _dnsServer.DirectQueryAsync(new DnsQuestionRecord(_name, DnsResourceRecordType.DS, DnsClass.IN)));

            List<DnssecPrivateKey> activePrivateKeys = new List<DnssecPrivateKey>(dsRecords.Count);

            foreach (DnsDSRecordData dsRecord in dsRecords)
            {
                foreach (DnssecPrivateKey privateKey in privateKeys)
                {
                    if ((dsRecord.KeyTag == privateKey.DnsKey.ComputedKeyTag) && (dsRecord.Algorithm == privateKey.DnsKey.Algorithm) && privateKey.DnsKey.IsDnsKeyValid(_name, dsRecord))
                    {
                        activePrivateKeys.Add(privateKey);
                        break;
                    }
                }
            }

            return activePrivateKeys;
        }

        private bool TryRefreshAllSignatures()
        {
            List<DnsResourceRecord> addedRecords = new List<DnsResourceRecord>();
            List<DnsResourceRecord> deletedRecords = new List<DnsResourceRecord>();

            IReadOnlyList<AuthZone> zones = _dnsServer.AuthZoneManager.GetZoneWithSubDomainZones(_name);

            foreach (AuthZone zone in zones)
            {
                IReadOnlyList<DnsResourceRecord> newRRSigRecords = zone.RefreshSignatures();
                if (newRRSigRecords.Count > 0)
                {
                    zone.AddOrUpdateRRSigRecords(newRRSigRecords, out IReadOnlyList<DnsResourceRecord> deletedRRSigRecords);

                    addedRecords.AddRange(newRRSigRecords);
                    deletedRecords.AddRange(deletedRRSigRecords);
                }
            }

            if ((deletedRecords.Count > 0) || (addedRecords.Count > 0))
            {
                CommitAndIncrementSerial(deletedRecords, addedRecords);
                TriggerNotify();

                return true;
            }

            return false;
        }

        internal override IReadOnlyList<DnsResourceRecord> SignRRSet(IReadOnlyList<DnsResourceRecord> records)
        {
            DnsResourceRecordType rrsetType = records[0].Type;

            List<DnsResourceRecord> rrsigRecords = new List<DnsResourceRecord>();
            uint signatureValidityPeriod = GetSignatureValidityPeriod();

            switch (rrsetType)
            {
                case DnsResourceRecordType.DNSKEY:
                    lock (_dnssecPrivateKeys)
                    {
                        foreach (KeyValuePair<ushort, DnssecPrivateKey> privateKeyEntry in _dnssecPrivateKeys)
                        {
                            DnssecPrivateKey privateKey = privateKeyEntry.Value;
                            if (privateKey.KeyType != DnssecPrivateKeyType.KeySigningKey)
                                continue;

                            switch (privateKey.State)
                            {
                                case DnssecPrivateKeyState.Generated:
                                case DnssecPrivateKeyState.Published:
                                case DnssecPrivateKeyState.Ready:
                                case DnssecPrivateKeyState.Active:
                                case DnssecPrivateKeyState.Revoked:
                                    rrsigRecords.Add(privateKey.SignRRSet(_name, records, DNSSEC_SIGNATURE_INCEPTION_OFFSET, signatureValidityPeriod));
                                    break;
                            }
                        }
                    }
                    break;

                case DnsResourceRecordType.RRSIG:
                    throw new InvalidOperationException();

                case DnsResourceRecordType.ANAME:
                case DnsResourceRecordType.APP:
                    throw new DnsServerException("Cannot sign RRSet: The record type [" + rrsetType.ToString() + "] is not supported by DNSSEC signed primary zones.");

                default:
                    if ((rrsetType == DnsResourceRecordType.NS) && (records[0].Name.Length > _name.Length))
                        return Array.Empty<DnsResourceRecord>(); //referrer NS records are not signed

                    lock (_dnssecPrivateKeys)
                    {
                        foreach (KeyValuePair<ushort, DnssecPrivateKey> privateKeyEntry in _dnssecPrivateKeys)
                        {
                            DnssecPrivateKey privateKey = privateKeyEntry.Value;
                            if (privateKey.KeyType != DnssecPrivateKeyType.ZoneSigningKey)
                                continue;

                            switch (privateKey.State)
                            {
                                case DnssecPrivateKeyState.Ready:
                                case DnssecPrivateKeyState.Active:
                                    rrsigRecords.Add(privateKey.SignRRSet(_name, records, DNSSEC_SIGNATURE_INCEPTION_OFFSET, signatureValidityPeriod));
                                    break;
                            }
                        }
                    }
                    break;
            }

            if (rrsigRecords.Count == 0)
                throw new InvalidOperationException("Cannot sign RRSet: no private key was available.");

            return rrsigRecords;
        }

        internal void UpdateDnssecRecordsFor(AuthZone zone, DnsResourceRecordType type)
        {
            //lock to sync this call to prevent inconsistent NSEC/NSEC3 updates
            lock (_dnssecUpdateLock)
            {
                IReadOnlyList<DnsResourceRecord> records = zone.GetRecords(type);
                if (records.Count > 0)
                {
                    //rrset added or updated
                    //sign rrset
                    IReadOnlyList<DnsResourceRecord> newRRSigRecords = SignRRSet(records);
                    if (newRRSigRecords.Count > 0)
                    {
                        zone.AddOrUpdateRRSigRecords(newRRSigRecords, out IReadOnlyList<DnsResourceRecord> deletedRRSigRecords);

                        CommitAndIncrementSerial(deletedRRSigRecords, newRRSigRecords);
                    }
                }
                else
                {
                    //rrset deleted
                    //delete rrsig
                    IReadOnlyList<DnsResourceRecord> existingRRSigRecords = zone.GetRecords(DnsResourceRecordType.RRSIG);
                    if (existingRRSigRecords.Count > 0)
                    {
                        List<DnsResourceRecord> recordsToDelete = new List<DnsResourceRecord>();

                        foreach (DnsResourceRecord existingRRSigRecord in existingRRSigRecords)
                        {
                            DnsRRSIGRecordData rrsig = existingRRSigRecord.RDATA as DnsRRSIGRecordData;
                            if (rrsig.TypeCovered == type)
                                recordsToDelete.Add(existingRRSigRecord);
                        }

                        if (zone.TryDeleteRecords(DnsResourceRecordType.RRSIG, recordsToDelete, out IReadOnlyList<DnsResourceRecord> deletedRRSigRecords))
                            CommitAndIncrementSerial(deletedRRSigRecords);
                    }
                }

                if (_dnssecStatus == AuthZoneDnssecStatus.SignedWithNSEC)
                {
                    UpdateNSecRRSetFor(zone);
                }
                else
                {
                    UpdateNSec3RRSetFor(zone);

                    int apexLabelCount = DnsRRSIGRecordData.GetLabelCount(_name);
                    int zoneLabelCount = DnsRRSIGRecordData.GetLabelCount(zone.Name);

                    if ((zoneLabelCount - apexLabelCount) > 1)
                    {
                        //empty non-terminal (ENT) may exists
                        string currentOwnerName = zone.Name;

                        while (true)
                        {
                            currentOwnerName = AuthZoneManager.GetParentZone(currentOwnerName);
                            if (currentOwnerName.Equals(_name, StringComparison.OrdinalIgnoreCase))
                                break;

                            //update NSEC3 rrset for current owner name
                            AuthZone entZone = _dnsServer.AuthZoneManager.GetAuthZone(_name, currentOwnerName);
                            if (entZone is null)
                                entZone = new PrimarySubDomainZone(null, currentOwnerName); //dummy empty non-terminal (ENT) sub domain object

                            UpdateNSec3RRSetFor(entZone);
                        }
                    }
                }
            }
        }

        private void UpdateNSecRRSetFor(AuthZone zone)
        {
            uint ttl = GetZoneSoaMinimum();

            IReadOnlyList<DnsResourceRecord> newNSecRecords = GetUpdatedNSecRRSetFor(zone, ttl);
            if (newNSecRecords.Count > 0)
            {
                DnsResourceRecord newNSecRecord = newNSecRecords[0];
                DnsNSECRecordData newNSec = newNSecRecord.RDATA as DnsNSECRecordData;
                if (newNSec.Types.Count == 2)
                {
                    //only NSEC and RRSIG exists so remove NSEC
                    IReadOnlyList<DnsResourceRecord> deletedNSecRecords = zone.RemoveNSecRecordsWithRRSig();
                    if (deletedNSecRecords.Count > 0)
                        CommitAndIncrementSerial(deletedNSecRecords);

                    //relink previous nsec
                    RelinkPreviousNSecRRSetFor(newNSecRecord, ttl, true);
                }
                else
                {
                    List<DnsResourceRecord> addedRecords = new List<DnsResourceRecord>();
                    List<DnsResourceRecord> deletedRecords = new List<DnsResourceRecord>();

                    if (!zone.TrySetRecords(DnsResourceRecordType.NSEC, newNSecRecords, out IReadOnlyList<DnsResourceRecord> deletedNSecRecords))
                        throw new DnsServerException("Failed to set DNSSEC records. Please try again.");

                    addedRecords.AddRange(newNSecRecords);
                    deletedRecords.AddRange(deletedNSecRecords);

                    IReadOnlyList<DnsResourceRecord> newRRSigRecords = SignRRSet(newNSecRecords);
                    if (newRRSigRecords.Count > 0)
                    {
                        zone.AddOrUpdateRRSigRecords(newRRSigRecords, out IReadOnlyList<DnsResourceRecord> deletedRRSigRecords);

                        addedRecords.AddRange(newRRSigRecords);
                        deletedRecords.AddRange(deletedRRSigRecords);
                    }

                    CommitAndIncrementSerial(deletedRecords, addedRecords);

                    if (deletedNSecRecords.Count == 0)
                    {
                        //new NSEC created since no old NSEC was removed
                        //relink previous nsec
                        RelinkPreviousNSecRRSetFor(newNSecRecord, ttl, false);
                    }
                }
            }
        }

        private void UpdateNSec3RRSetFor(AuthZone zone)
        {
            uint ttl = GetZoneSoaMinimum();
            bool noSubDomainExistsForEmptyZone = (zone.IsEmpty || zone.HasOnlyNSec3Records()) && !_dnsServer.AuthZoneManager.SubDomainExists(_name, zone.Name);

            IReadOnlyList<DnsResourceRecord> newNSec3Records = GetUpdatedNSec3RRSetFor(zone, ttl, noSubDomainExistsForEmptyZone);
            if (newNSec3Records.Count > 0)
            {
                DnsResourceRecord newNSec3Record = newNSec3Records[0];

                AuthZone nsec3Zone = _dnsServer.AuthZoneManager.GetOrAddSubDomainZone(_name, newNSec3Record.Name);
                if (nsec3Zone is null)
                    throw new InvalidOperationException();

                if (noSubDomainExistsForEmptyZone)
                {
                    //no records exists in real zone and no sub domain exists, so remove NSEC3
                    IReadOnlyList<DnsResourceRecord> deletedNSec3Records = nsec3Zone.RemoveNSec3RecordsWithRRSig();
                    if (deletedNSec3Records.Count > 0)
                        CommitAndIncrementSerial(deletedNSec3Records);

                    //remove nsec3 sub domain zone if empty since it wont get removed otherwise
                    if (nsec3Zone is SubDomainZone nsec3SubDomainZone)
                    {
                        if (nsec3Zone.IsEmpty)
                            _dnsServer.AuthZoneManager.RemoveSubDomainZone(nsec3Zone.Name); //remove empty sub zone
                        else
                            nsec3SubDomainZone.AutoUpdateState();
                    }

                    //remove the real zone if empty so that any of the ENT that exists can also be removed later
                    if (zone is SubDomainZone subDomainZone)
                    {
                        if (zone.IsEmpty)
                            _dnsServer.AuthZoneManager.RemoveSubDomainZone(zone.Name); //remove empty sub zone
                        else
                            subDomainZone.AutoUpdateState();
                    }

                    //relink previous nsec3
                    RelinkPreviousNSec3RRSet(newNSec3Record, ttl, true);
                }
                else
                {
                    List<DnsResourceRecord> addedRecords = new List<DnsResourceRecord>();
                    List<DnsResourceRecord> deletedRecords = new List<DnsResourceRecord>();

                    if (!nsec3Zone.TrySetRecords(DnsResourceRecordType.NSEC3, newNSec3Records, out IReadOnlyList<DnsResourceRecord> deletedNSec3Records))
                        throw new DnsServerException("Failed to set DNSSEC records. Please try again.");

                    addedRecords.AddRange(newNSec3Records);
                    deletedRecords.AddRange(deletedNSec3Records);

                    IReadOnlyList<DnsResourceRecord> newRRSigRecords = SignRRSet(newNSec3Records);
                    if (newRRSigRecords.Count > 0)
                    {
                        nsec3Zone.AddOrUpdateRRSigRecords(newRRSigRecords, out IReadOnlyList<DnsResourceRecord> deletedRRSigRecords);

                        addedRecords.AddRange(newRRSigRecords);
                        deletedRecords.AddRange(deletedRRSigRecords);
                    }

                    CommitAndIncrementSerial(deletedRecords, addedRecords);

                    if (deletedNSec3Records.Count == 0)
                    {
                        //new NSEC3 created since no old NSEC3 was removed
                        //relink previous nsec
                        RelinkPreviousNSec3RRSet(newNSec3Record, ttl, false);
                    }
                }
            }
        }

        private IReadOnlyList<DnsResourceRecord> GetUpdatedNSecRRSetFor(AuthZone zone, uint ttl)
        {
            AuthZone nextZone = _dnsServer.AuthZoneManager.FindNextSubDomainZone(_name, zone.Name);
            if (nextZone is null)
                nextZone = this;

            return zone.GetUpdatedNSecRRSet(nextZone.Name, ttl);
        }

        private IReadOnlyList<DnsResourceRecord> GetUpdatedNSec3RRSetFor(AuthZone zone, uint ttl, bool forceGetNewRRSet)
        {
            if (!_entries.TryGetValue(DnsResourceRecordType.NSEC3PARAM, out IReadOnlyList<DnsResourceRecord> nsec3ParamRecords))
                throw new InvalidOperationException();

            DnsResourceRecord nsec3ParamRecord = nsec3ParamRecords[0];
            DnsNSEC3PARAMRecordData nsec3Param = nsec3ParamRecord.RDATA as DnsNSEC3PARAMRecordData;

            string hashedOwnerName = nsec3Param.ComputeHashedOwnerNameBase32HexString(zone.Name) + (_name.Length > 0 ? "." + _name : "");
            byte[] nextHashedOwnerName = null;

            //find next hashed owner name
            string currentOwnerName = hashedOwnerName;

            while (true)
            {
                AuthZone nextZone = _dnsServer.AuthZoneManager.FindNextSubDomainZone(_name, currentOwnerName);
                if (nextZone is null)
                    break;

                IReadOnlyList<DnsResourceRecord> nextNSec3Records = nextZone.GetRecords(DnsResourceRecordType.NSEC3);
                if (nextNSec3Records.Count > 0)
                {
                    nextHashedOwnerName = DnsNSEC3RecordData.GetHashedOwnerNameFrom(nextNSec3Records[0].Name);
                    break;
                }

                currentOwnerName = nextZone.Name;
            }

            if (nextHashedOwnerName is null)
            {
                //didnt find next NSEC3 record since current must be last; find the first NSEC3 record
                DnsResourceRecord previousNSec3Record = null;

                while (true)
                {
                    AuthZone previousZone = _dnsServer.AuthZoneManager.FindPreviousSubDomainZone(_name, currentOwnerName);
                    if (previousZone is null)
                        break;

                    IReadOnlyList<DnsResourceRecord> previousNSec3Records = previousZone.GetRecords(DnsResourceRecordType.NSEC3);
                    if (previousNSec3Records.Count > 0)
                        previousNSec3Record = previousNSec3Records[0];

                    currentOwnerName = previousZone.Name;
                }

                if (previousNSec3Record is not null)
                    nextHashedOwnerName = DnsNSEC3RecordData.GetHashedOwnerNameFrom(previousNSec3Record.Name);
            }

            if (nextHashedOwnerName is null)
                nextHashedOwnerName = DnsNSEC3RecordData.GetHashedOwnerNameFrom(hashedOwnerName); //only 1 NSEC3 record in zone

            IReadOnlyList<DnsResourceRecord> newNSec3Records = zone.CreateNSec3RRSet(hashedOwnerName, nextHashedOwnerName, ttl, nsec3Param.Iterations, nsec3Param.SaltValue);

            if (forceGetNewRRSet)
                return newNSec3Records;

            AuthZone nsec3Zone = _dnsServer.AuthZoneManager.GetAuthZone(_name, hashedOwnerName);
            if (nsec3Zone is null)
                return newNSec3Records;

            return nsec3Zone.GetUpdatedNSec3RRSet(newNSec3Records);
        }

        private void RelinkPreviousNSecRRSetFor(DnsResourceRecord currentNSecRecord, uint ttl, bool wasRemoved)
        {
            AuthZone previousNsecZone = _dnsServer.AuthZoneManager.FindPreviousSubDomainZone(_name, currentNSecRecord.Name);
            if (previousNsecZone is null)
                return; //current zone is apex

            IReadOnlyList<DnsResourceRecord> newPreviousNSecRecords;

            if (wasRemoved)
                newPreviousNSecRecords = previousNsecZone.GetUpdatedNSecRRSet((currentNSecRecord.RDATA as DnsNSECRecordData).NextDomainName, ttl);
            else
                newPreviousNSecRecords = previousNsecZone.GetUpdatedNSecRRSet(currentNSecRecord.Name, ttl);

            if (newPreviousNSecRecords.Count > 0)
            {
                if (!previousNsecZone.TrySetRecords(DnsResourceRecordType.NSEC, newPreviousNSecRecords, out IReadOnlyList<DnsResourceRecord> deletedNSecRecords))
                    throw new DnsServerException("Failed to set DNSSEC records. Please try again.");

                List<DnsResourceRecord> addedRecords = new List<DnsResourceRecord>();
                List<DnsResourceRecord> deletedRecords = new List<DnsResourceRecord>();

                addedRecords.AddRange(newPreviousNSecRecords);
                deletedRecords.AddRange(deletedNSecRecords);

                IReadOnlyList<DnsResourceRecord> newRRSigRecords = SignRRSet(newPreviousNSecRecords);
                if (newRRSigRecords.Count > 0)
                {
                    previousNsecZone.AddOrUpdateRRSigRecords(newRRSigRecords, out IReadOnlyList<DnsResourceRecord> deletedRRSigRecords);

                    addedRecords.AddRange(newRRSigRecords);
                    deletedRecords.AddRange(deletedRRSigRecords);
                }

                CommitAndIncrementSerial(deletedRecords, addedRecords);
            }
        }

        private void RelinkPreviousNSec3RRSet(DnsResourceRecord currentNSec3Record, uint ttl, bool wasRemoved)
        {
            DnsNSEC3RecordData currentNSec3 = currentNSec3Record.RDATA as DnsNSEC3RecordData;

            //find the previous NSEC3 and update it
            DnsResourceRecord previousNSec3Record = null;
            AuthZone previousNSec3Zone;
            string currentOwnerName = currentNSec3Record.Name;

            while (true)
            {
                previousNSec3Zone = _dnsServer.AuthZoneManager.FindPreviousSubDomainZone(_name, currentOwnerName);
                if (previousNSec3Zone is null)
                    break;

                IReadOnlyList<DnsResourceRecord> previousNSec3Records = previousNSec3Zone.GetRecords(DnsResourceRecordType.NSEC3);
                if (previousNSec3Records.Count > 0)
                {
                    previousNSec3Record = previousNSec3Records[0];
                    break;
                }

                currentOwnerName = previousNSec3Zone.Name;
            }

            if (previousNSec3Record is null)
            {
                //didnt find previous NSEC3; find the last NSEC3 to update
                if (wasRemoved)
                    currentOwnerName = currentNSec3.NextHashedOwnerName + (_name.Length > 0 ? "." + _name : "");
                else
                    currentOwnerName = currentNSec3Record.Name;

                while (true)
                {
                    AuthZone nextNSec3Zone = _dnsServer.AuthZoneManager.GetAuthZone(_name, currentOwnerName);
                    if (nextNSec3Zone is null)
                        break;

                    IReadOnlyList<DnsResourceRecord> nextNSec3Records = nextNSec3Zone.GetRecords(DnsResourceRecordType.NSEC3);
                    if (nextNSec3Records.Count > 0)
                    {
                        previousNSec3Record = nextNSec3Records[0];
                        previousNSec3Zone = nextNSec3Zone;

                        string nextHashedOwnerNameString = (previousNSec3Record.RDATA as DnsNSEC3RecordData).NextHashedOwnerName + (_name.Length > 0 ? "." + _name : "");
                        if (DnsNSECRecordData.CanonicalComparison(previousNSec3Record.Name, nextHashedOwnerNameString) >= 0)
                            break; //found last NSEC3

                        //jump to next hashed owner
                        currentOwnerName = nextHashedOwnerNameString;
                    }
                    else
                    {
                        currentOwnerName = nextNSec3Zone.Name;
                    }
                }
            }

            if (previousNSec3Record is null)
                throw new InvalidOperationException();

            DnsNSEC3RecordData previousNSec3 = previousNSec3Record.RDATA as DnsNSEC3RecordData;
            DnsNSEC3RecordData newPreviousNSec3;

            if (wasRemoved)
                newPreviousNSec3 = new DnsNSEC3RecordData(DnssecNSEC3HashAlgorithm.SHA1, DnssecNSEC3Flags.None, previousNSec3.Iterations, previousNSec3.SaltValue, currentNSec3.NextHashedOwnerNameValue, previousNSec3.Types);
            else
                newPreviousNSec3 = new DnsNSEC3RecordData(DnssecNSEC3HashAlgorithm.SHA1, DnssecNSEC3Flags.None, previousNSec3.Iterations, previousNSec3.SaltValue, DnsNSEC3RecordData.GetHashedOwnerNameFrom(currentNSec3Record.Name), previousNSec3.Types);

            DnsResourceRecord[] newPreviousNSec3Records = new DnsResourceRecord[] { new DnsResourceRecord(previousNSec3Record.Name, DnsResourceRecordType.NSEC3, DnsClass.IN, ttl, newPreviousNSec3) };

            if (!previousNSec3Zone.TrySetRecords(DnsResourceRecordType.NSEC3, newPreviousNSec3Records, out IReadOnlyList<DnsResourceRecord> deletedNSec3Records))
                throw new DnsServerException("Failed to set DNSSEC records. Please try again.");

            List<DnsResourceRecord> addedRecords = new List<DnsResourceRecord>();
            List<DnsResourceRecord> deletedRecords = new List<DnsResourceRecord>();

            addedRecords.AddRange(newPreviousNSec3Records);
            deletedRecords.AddRange(deletedNSec3Records);

            IReadOnlyList<DnsResourceRecord> newRRSigRecords = SignRRSet(newPreviousNSec3Records);
            if (newRRSigRecords.Count > 0)
            {
                previousNSec3Zone.AddOrUpdateRRSigRecords(newRRSigRecords, out IReadOnlyList<DnsResourceRecord> deletedRRSigRecords);

                addedRecords.AddRange(newRRSigRecords);
                deletedRecords.AddRange(deletedRRSigRecords);
            }

            CommitAndIncrementSerial(deletedRecords, addedRecords);
        }

        private uint GetSignatureValidityPeriod()
        {
            //SOA EXPIRE + 3 days
            return (_entries[DnsResourceRecordType.SOA][0].RDATA as DnsSOARecordData).Expire + (3 * 24 * 60 * 60);
        }

        private uint GetZoneSoaMinimum()
        {
            return GetZoneSoaMinimum(_entries[DnsResourceRecordType.SOA][0]);
        }

        private uint GetZoneSoaMinimum(DnsResourceRecord soaRecord)
        {
            return Math.Min((soaRecord.RDATA as DnsSOARecordData).Minimum, soaRecord.OriginalTtlValue);
        }

        internal uint GetZoneSoaExpire()
        {
            return (_entries[DnsResourceRecordType.SOA][0].RDATA as DnsSOARecordData).Expire;
        }

        public uint GetDnsKeyTtl()
        {
            if (_entries.TryGetValue(DnsResourceRecordType.DNSKEY, out IReadOnlyList<DnsResourceRecord> dnsKeyRecords))
                return dnsKeyRecords[0].OriginalTtlValue;

            return 24 * 60 * 60;
        }

        public void UpdateDnsKeyTtl(uint dnsKeyTtl)
        {
            if (_dnssecStatus == AuthZoneDnssecStatus.Unsigned)
                throw new DnsServerException("The zone must be signed.");

            lock (_dnssecPrivateKeys)
            {
                foreach (KeyValuePair<ushort, DnssecPrivateKey> privateKeyEntry in _dnssecPrivateKeys)
                {
                    switch (privateKeyEntry.Value.State)
                    {
                        case DnssecPrivateKeyState.Ready:
                        case DnssecPrivateKeyState.Active:
                            break;

                        default:
                            throw new DnsServerException("Cannot update DNSKEY TTL value: one or more private keys have state other than Ready or Active.");
                    }
                }
            }

            if (!_entries.TryGetValue(DnsResourceRecordType.DNSKEY, out IReadOnlyList<DnsResourceRecord> dnsKeyRecords))
                throw new InvalidOperationException();

            DnsResourceRecord[] newDnsKeyRecords = new DnsResourceRecord[dnsKeyRecords.Count];

            for (int i = 0; i < dnsKeyRecords.Count; i++)
            {
                DnsResourceRecord dnsKeyRecord = dnsKeyRecords[i];
                newDnsKeyRecords[i] = new DnsResourceRecord(dnsKeyRecord.Name, DnsResourceRecordType.DNSKEY, DnsClass.IN, dnsKeyTtl, dnsKeyRecord.RDATA);
            }

            if (!TrySetRecords(DnsResourceRecordType.DNSKEY, newDnsKeyRecords, out IReadOnlyList<DnsResourceRecord> deletedRecords))
                throw new DnsServerException("Failed to update DNSKEY TTL. Please try again.");

            CommitAndIncrementSerial(deletedRecords, newDnsKeyRecords);
        }

        #endregion

        #region versioning

        internal void CommitAndIncrementSerial(IReadOnlyList<DnsResourceRecord> deletedRecords = null, IReadOnlyList<DnsResourceRecord> addedRecords = null)
        {
            if (_internal)
                return;

            lock (_history)
            {
                DnsResourceRecord oldSoaRecord = _entries[DnsResourceRecordType.SOA][0];
                DnsResourceRecord newSoaRecord;
                {
                    DnsSOARecordData soa = oldSoaRecord.RDATA as DnsSOARecordData;

                    uint serial = soa.Serial;
                    if (serial < uint.MaxValue)
                        serial++;
                    else
                        serial = 1;

                    newSoaRecord = new DnsResourceRecord(oldSoaRecord.Name, oldSoaRecord.Type, oldSoaRecord.Class, oldSoaRecord.TtlValue, new DnsSOARecordData(soa.PrimaryNameServer, soa.ResponsiblePerson, serial, soa.Refresh, soa.Retry, soa.Expire, soa.Minimum)) { Tag = oldSoaRecord.Tag };
                    oldSoaRecord.Tag = null; //remove RR info from old SOA to allow creating new RR info for it during SetDeletedOn()
                }

                DnsResourceRecord[] newSoaRecords = new DnsResourceRecord[] { newSoaRecord };

                //update SOA
                _entries[DnsResourceRecordType.SOA] = newSoaRecords;

                IReadOnlyList<DnsResourceRecord> newRRSigRecords = null;
                IReadOnlyList<DnsResourceRecord> deletedRRSigRecords = null;

                if (_dnssecStatus != AuthZoneDnssecStatus.Unsigned)
                {
                    //sign SOA and update RRSig
                    newRRSigRecords = SignRRSet(newSoaRecords);
                    AddOrUpdateRRSigRecords(newRRSigRecords, out deletedRRSigRecords);
                }

                //start commit
                oldSoaRecord.SetDeletedOn(DateTime.UtcNow);

                //write removed
                _history.Add(oldSoaRecord);

                if (deletedRecords is not null)
                {
                    foreach (DnsResourceRecord deletedRecord in deletedRecords)
                    {
                        if (deletedRecord.IsDisabled())
                            continue;

                        _history.Add(deletedRecord);

                        if (deletedRecord.Type == DnsResourceRecordType.NS)
                            _history.AddRange(deletedRecord.GetGlueRecords());
                    }
                }

                if (deletedRRSigRecords is not null)
                    _history.AddRange(deletedRRSigRecords);

                //write added
                _history.Add(newSoaRecord);

                if (addedRecords is not null)
                {
                    foreach (DnsResourceRecord addedRecord in addedRecords)
                    {
                        if (addedRecord.IsDisabled())
                            continue;

                        _history.Add(addedRecord);

                        if (addedRecord.Type == DnsResourceRecordType.NS)
                            _history.AddRange(addedRecord.GetGlueRecords());
                    }
                }

                if (newRRSigRecords is not null)
                    _history.AddRange(newRRSigRecords);

                //end commit

                CleanupHistory(_history);
            }
        }

        #endregion

        #region public

        public void TriggerNotify()
        {
            if (_disabled)
                return;

            if (_notify == AuthZoneNotify.None)
                return;

            if (_notifyTimerTriggered)
                return;

            _notifyTimer.Change(NOTIFY_TIMER_INTERVAL, Timeout.Infinite);
            _notifyTimerTriggered = true;
        }

        public override void SetRecords(DnsResourceRecordType type, IReadOnlyList<DnsResourceRecord> records)
        {
            if (_dnssecStatus != AuthZoneDnssecStatus.Unsigned)
            {
                switch (type)
                {
                    case DnsResourceRecordType.ANAME:
                    case DnsResourceRecordType.APP:
                        throw new DnsServerException("The record type is not supported by DNSSEC signed primary zones.");

                    default:
                        foreach (DnsResourceRecord record in records)
                        {
                            if (record.IsDisabled())
                                throw new DnsServerException("Cannot set records: disabling records in a signed zones is not supported.");
                        }

                        break;
                }
            }

            switch (type)
            {
                case DnsResourceRecordType.CNAME:
                case DnsResourceRecordType.DS:
                    throw new InvalidOperationException("Cannot set " + type.ToString() + " record at zone apex.");

                case DnsResourceRecordType.SOA:
                    if ((records.Count != 1) || !records[0].Name.Equals(_name, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Invalid SOA record.");

                    DnsResourceRecord soaRecord = records[0];
                    DnsSOARecordData soa = soaRecord.RDATA as DnsSOARecordData;

                    if (soaRecord.OriginalTtlValue > soa.Expire)
                        throw new DnsServerException("Failed to set records: TTL cannot be greater than SOA EXPIRE.");

                    //remove any resource record info except comments
                    string comments = soaRecord.GetComments();
                    soaRecord.Tag = null;
                    soaRecord.SetComments(comments);

                    uint oldSoaMinimum = GetZoneSoaMinimum();

                    base.SetRecords(type, records);

                    CommitAndIncrementSerial();

                    uint newSoaMinimum = GetZoneSoaMinimum(soaRecord);

                    if (oldSoaMinimum != newSoaMinimum)
                    {
                        switch (_dnssecStatus)
                        {
                            case AuthZoneDnssecStatus.SignedWithNSEC:
                                RefreshNSec();
                                break;

                            case AuthZoneDnssecStatus.SignedWithNSEC3:
                                RefreshNSec3();
                                break;
                        }
                    }

                    TriggerNotify();
                    break;

                case DnsResourceRecordType.DNSKEY:
                case DnsResourceRecordType.RRSIG:
                case DnsResourceRecordType.NSEC:
                case DnsResourceRecordType.NSEC3PARAM:
                case DnsResourceRecordType.NSEC3:
                    throw new InvalidOperationException("Cannot set DNSSEC records.");

                case DnsResourceRecordType.FWD:
                    throw new DnsServerException("The record type is not supported by primary zones.");

                default:
                    if (records[0].OriginalTtlValue > GetZoneSoaExpire())
                        throw new DnsServerException("Failed to set records: TTL cannot be greater than SOA EXPIRE.");

                    if (!TrySetRecords(type, records, out IReadOnlyList<DnsResourceRecord> deletedRecords))
                        throw new DnsServerException("Failed to set records. Please try again.");

                    CommitAndIncrementSerial(deletedRecords, records);

                    if (_dnssecStatus != AuthZoneDnssecStatus.Unsigned)
                        UpdateDnssecRecordsFor(this, type);

                    TriggerNotify();
                    break;
            }
        }

        public override void AddRecord(DnsResourceRecord record)
        {
            if (_dnssecStatus != AuthZoneDnssecStatus.Unsigned)
            {
                switch (record.Type)
                {
                    case DnsResourceRecordType.ANAME:
                    case DnsResourceRecordType.APP:
                        throw new DnsServerException("The record type is not supported by DNSSEC signed primary zones.");

                    default:
                        if (record.IsDisabled())
                            throw new DnsServerException("Cannot add record: disabling records in a signed zones is not supported.");

                        break;
                }
            }

            switch (record.Type)
            {
                case DnsResourceRecordType.APP:
                    throw new InvalidOperationException("Cannot add record: use SetRecords() for " + record.Type.ToString() + " record");

                case DnsResourceRecordType.DS:
                    throw new InvalidOperationException("Cannot set DS record at zone apex.");

                case DnsResourceRecordType.DNSKEY:
                case DnsResourceRecordType.RRSIG:
                case DnsResourceRecordType.NSEC:
                case DnsResourceRecordType.NSEC3PARAM:
                case DnsResourceRecordType.NSEC3:
                    throw new InvalidOperationException("Cannot add DNSSEC record.");

                case DnsResourceRecordType.FWD:
                    throw new DnsServerException("The record type is not supported by primary zones.");

                default:
                    if (record.OriginalTtlValue > GetZoneSoaExpire())
                        throw new DnsServerException("Failed to add record: TTL cannot be greater than SOA EXPIRE.");

                    base.AddRecord(record);

                    CommitAndIncrementSerial(null, new DnsResourceRecord[] { record });

                    if (_dnssecStatus != AuthZoneDnssecStatus.Unsigned)
                        UpdateDnssecRecordsFor(this, record.Type);

                    TriggerNotify();
                    break;
            }
        }

        public override bool DeleteRecords(DnsResourceRecordType type)
        {
            switch (type)
            {
                case DnsResourceRecordType.SOA:
                    throw new InvalidOperationException("Cannot delete SOA record.");

                case DnsResourceRecordType.DNSKEY:
                case DnsResourceRecordType.RRSIG:
                case DnsResourceRecordType.NSEC:
                case DnsResourceRecordType.NSEC3PARAM:
                case DnsResourceRecordType.NSEC3:
                    throw new InvalidOperationException("Cannot delete DNSSEC records.");

                default:
                    if (_entries.TryRemove(type, out IReadOnlyList<DnsResourceRecord> removedRecords))
                    {
                        CommitAndIncrementSerial(removedRecords);

                        if (_dnssecStatus != AuthZoneDnssecStatus.Unsigned)
                            UpdateDnssecRecordsFor(this, type);

                        TriggerNotify();

                        return true;
                    }

                    return false;
            }
        }

        public override bool DeleteRecord(DnsResourceRecordType type, DnsResourceRecordData record)
        {
            switch (type)
            {
                case DnsResourceRecordType.SOA:
                    throw new InvalidOperationException("Cannot delete SOA record.");

                case DnsResourceRecordType.DNSKEY:
                case DnsResourceRecordType.RRSIG:
                case DnsResourceRecordType.NSEC:
                case DnsResourceRecordType.NSEC3PARAM:
                case DnsResourceRecordType.NSEC3:
                    throw new InvalidOperationException("Cannot delete DNSSEC records.");

                default:
                    if (TryDeleteRecord(type, record, out DnsResourceRecord deletedRecord))
                    {
                        CommitAndIncrementSerial(new DnsResourceRecord[] { deletedRecord });

                        if (_dnssecStatus != AuthZoneDnssecStatus.Unsigned)
                            UpdateDnssecRecordsFor(this, type);

                        TriggerNotify();

                        return true;
                    }

                    return false;
            }
        }

        public override void UpdateRecord(DnsResourceRecord oldRecord, DnsResourceRecord newRecord)
        {
            switch (oldRecord.Type)
            {
                case DnsResourceRecordType.SOA:
                    throw new InvalidOperationException("Cannot update record: use SetRecords() for " + oldRecord.Type.ToString() + " record");

                case DnsResourceRecordType.DNSKEY:
                case DnsResourceRecordType.RRSIG:
                case DnsResourceRecordType.NSEC:
                case DnsResourceRecordType.NSEC3PARAM:
                case DnsResourceRecordType.NSEC3:
                    throw new InvalidOperationException("Cannot update DNSSEC records.");

                default:
                    if (oldRecord.Type != newRecord.Type)
                        throw new InvalidOperationException("Old and new record types do not match.");

                    if ((_dnssecStatus != AuthZoneDnssecStatus.Unsigned) && newRecord.IsDisabled())
                        throw new DnsServerException("Cannot update record: disabling records in a signed zones is not supported.");

                    if (newRecord.OriginalTtlValue > GetZoneSoaExpire())
                        throw new DnsServerException("Cannot update record: TTL cannot be greater than SOA EXPIRE.");

                    if (!TryDeleteRecord(oldRecord.Type, oldRecord.RDATA, out DnsResourceRecord deletedRecord))
                        throw new InvalidOperationException("Cannot update record: the record does not exists to be updated.");

                    base.AddRecord(newRecord);

                    CommitAndIncrementSerial(new DnsResourceRecord[] { deletedRecord }, new DnsResourceRecord[] { newRecord });

                    if (_dnssecStatus != AuthZoneDnssecStatus.Unsigned)
                        UpdateDnssecRecordsFor(this, oldRecord.Type);

                    TriggerNotify();
                    break;
            }
        }

        public IReadOnlyList<DnsResourceRecord> GetHistory()
        {
            lock (_history)
            {
                return _history.ToArray();
            }
        }

        #endregion

        #region properties

        public bool Internal
        { get { return _internal; } }

        public override bool Disabled
        {
            get { return _disabled; }
            set
            {
                if (_disabled != value)
                {
                    _disabled = value;

                    if (_disabled)
                        _notifyTimer.Change(Timeout.Infinite, Timeout.Infinite);
                    else
                        TriggerNotify();
                }
            }
        }

        public override AuthZoneTransfer ZoneTransfer
        {
            get { return _zoneTransfer; }
            set
            {
                if (_internal)
                    throw new InvalidOperationException();

                _zoneTransfer = value;
            }
        }

        public override AuthZoneNotify Notify
        {
            get { return _notify; }
            set
            {
                if (_internal)
                    throw new InvalidOperationException();

                _notify = value;
            }
        }

        public IReadOnlyDictionary<string, object> TsigKeyNames
        {
            get { return _tsigKeyNames; }
            set { _tsigKeyNames = value; }
        }

        public IReadOnlyCollection<DnssecPrivateKey> DnssecPrivateKeys
        {
            get
            {
                if (_dnssecPrivateKeys is null)
                    return null;

                lock (_dnssecPrivateKeys)
                {
                    return _dnssecPrivateKeys.Values;
                }
            }
        }

        #endregion
    }
}
