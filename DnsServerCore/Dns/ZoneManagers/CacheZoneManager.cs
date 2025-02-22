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

using DnsServerCore.Dns.ResourceRecords;
using DnsServerCore.Dns.Trees;
using DnsServerCore.Dns.Zones;
using System.Collections.Generic;
using TechnitiumLibrary.Net.Dns;
using TechnitiumLibrary.Net.Dns.EDnsOptions;
using TechnitiumLibrary.Net.Dns.ResourceRecords;

namespace DnsServerCore.Dns.ZoneManagers
{
    public sealed class CacheZoneManager : DnsCache
    {
        #region variables

        public const uint FAILURE_RECORD_TTL = 60u;
        public const uint NEGATIVE_RECORD_TTL = 300u;
        public const uint MINIMUM_RECORD_TTL = 10u;
        public const uint MAXIMUM_RECORD_TTL = 7 * 24 * 60 * 60;
        public const uint SERVE_STALE_TTL = 3 * 24 * 60 * 60; //3 days serve stale ttl as per https://www.rfc-editor.org/rfc/rfc8767.html suggestion

        readonly DnsServer _dnsServer;

        readonly CacheZoneTree _root = new CacheZoneTree();

        #endregion

        #region constructor

        public CacheZoneManager(DnsServer dnsServer)
            : base(FAILURE_RECORD_TTL, NEGATIVE_RECORD_TTL, MINIMUM_RECORD_TTL, MAXIMUM_RECORD_TTL, SERVE_STALE_TTL)
        {
            _dnsServer = dnsServer;
        }

        #endregion

        #region protected

        protected override void CacheRecords(IReadOnlyList<DnsResourceRecord> resourceRecords)
        {
            //read and set glue records from base class
            foreach (DnsResourceRecord resourceRecord in resourceRecords)
            {
                IReadOnlyList<DnsResourceRecord> glueRecords = GetGlueRecordsFrom(resourceRecord);
                IReadOnlyList<DnsResourceRecord> rrsigRecords = GetRRSIGRecordsFrom(resourceRecord);
                IReadOnlyList<DnsResourceRecord> nsecRecords = GetNSECRecordsFrom(resourceRecord);

                if ((glueRecords is not null) || (rrsigRecords is not null) || (nsecRecords is not null))
                {
                    DnsResourceRecordInfo rrInfo = resourceRecord.GetRecordInfo();

                    rrInfo.GlueRecords = glueRecords;
                    rrInfo.RRSIGRecords = rrsigRecords;
                    rrInfo.NSECRecords = nsecRecords;

                    if (glueRecords is not null)
                    {
                        foreach (DnsResourceRecord glueRecord in glueRecords)
                        {
                            IReadOnlyList<DnsResourceRecord> glueRRSIGRecords = GetRRSIGRecordsFrom(glueRecord);
                            if (glueRRSIGRecords is not null)
                                glueRecord.GetRecordInfo().RRSIGRecords = glueRRSIGRecords;
                        }
                    }

                    if (nsecRecords is not null)
                    {
                        foreach (DnsResourceRecord nsecRecord in nsecRecords)
                        {
                            IReadOnlyList<DnsResourceRecord> nsecRRSIGRecords = GetRRSIGRecordsFrom(nsecRecord);
                            if (nsecRRSIGRecords is not null)
                                nsecRecord.GetRecordInfo().RRSIGRecords = nsecRRSIGRecords;
                        }
                    }
                }
            }

            if (resourceRecords.Count == 1)
            {
                DnsResourceRecord resourceRecord = resourceRecords[0];

                CacheZone zone = _root.GetOrAdd(resourceRecord.Name, delegate (string key)
                {
                    return new CacheZone(resourceRecord.Name, 1);
                });

                zone.SetRecords(resourceRecord.Type, resourceRecords, _dnsServer.ServeStale);
            }
            else
            {
                Dictionary<string, Dictionary<DnsResourceRecordType, List<DnsResourceRecord>>> groupedByDomainRecords = DnsResourceRecord.GroupRecords(resourceRecords);
                bool serveStale = _dnsServer.ServeStale;

                //add grouped records
                foreach (KeyValuePair<string, Dictionary<DnsResourceRecordType, List<DnsResourceRecord>>> groupedByTypeRecords in groupedByDomainRecords)
                {
                    CacheZone zone = _root.GetOrAdd(groupedByTypeRecords.Key, delegate (string key)
                    {
                        return new CacheZone(groupedByTypeRecords.Key, groupedByTypeRecords.Value.Count);
                    });

                    foreach (KeyValuePair<DnsResourceRecordType, List<DnsResourceRecord>> groupedRecords in groupedByTypeRecords.Value)
                        zone.SetRecords(groupedRecords.Key, groupedRecords.Value, serveStale);
                }
            }
        }

        #endregion

        #region private

        private static IReadOnlyList<DnsResourceRecord> AddDSRecordsTo(CacheZone delegation, bool serveStale, IReadOnlyList<DnsResourceRecord> nsRecords)
        {
            IReadOnlyList<DnsResourceRecord> records = delegation.QueryRecords(DnsResourceRecordType.DS, serveStale, true);
            if ((records.Count > 0) && (records[0].Type == DnsResourceRecordType.DS))
            {
                List<DnsResourceRecord> newNSRecords = new List<DnsResourceRecord>(nsRecords.Count + records.Count);

                newNSRecords.AddRange(nsRecords);
                newNSRecords.AddRange(records);

                return newNSRecords;
            }

            //no DS records found check for NSEC records
            IReadOnlyList<DnsResourceRecord> nsecRecords = nsRecords[0].GetRecordInfo().NSECRecords;
            if (nsecRecords is not null)
            {
                List<DnsResourceRecord> newNSRecords = new List<DnsResourceRecord>(nsRecords.Count + nsecRecords.Count);

                newNSRecords.AddRange(nsRecords);
                newNSRecords.AddRange(nsecRecords);

                return newNSRecords;
            }

            //found nothing; return original NS records
            return nsRecords;
        }

        private void ResolveCNAME(DnsQuestionRecord question, DnsResourceRecord lastCNAME, bool serveStale, List<DnsResourceRecord> answerRecords)
        {
            int queryCount = 0;

            do
            {
                if (!_root.TryGet((lastCNAME.RDATA as DnsCNAMERecordData).Domain, out CacheZone cacheZone))
                    break;

                IReadOnlyList<DnsResourceRecord> records = cacheZone.QueryRecords(question.Type, serveStale, true);
                if (records.Count < 1)
                    break;

                answerRecords.AddRange(records);

                DnsResourceRecord lastRR = records[records.Count - 1];

                if (lastRR.Type != DnsResourceRecordType.CNAME)
                    break;

                lastCNAME = lastRR;
            }
            while (++queryCount < DnsServer.MAX_CNAME_HOPS);
        }

        private bool DoDNAMESubstitution(DnsQuestionRecord question, IReadOnlyList<DnsResourceRecord> answer, bool serveStale, out IReadOnlyList<DnsResourceRecord> newAnswer)
        {
            DnsResourceRecord dnameRR = answer[0];

            string result = (dnameRR.RDATA as DnsDNAMERecordData).Substitute(question.Name, dnameRR.Name);

            if (DnsClient.IsDomainNameValid(result))
            {
                DnsResourceRecord cnameRR = new DnsResourceRecord(question.Name, DnsResourceRecordType.CNAME, question.Class, dnameRR.TtlValue, new DnsCNAMERecordData(result));

                List<DnsResourceRecord> list = new List<DnsResourceRecord>(5)
                {
                    dnameRR,
                    cnameRR
                };

                ResolveCNAME(question, cnameRR, serveStale, list);

                newAnswer = list;
                return true;
            }
            else
            {
                newAnswer = answer;
                return false;
            }
        }

        private IReadOnlyList<DnsResourceRecord> GetAdditionalRecords(IReadOnlyList<DnsResourceRecord> refRecords, bool serveStale)
        {
            List<DnsResourceRecord> additionalRecords = new List<DnsResourceRecord>();

            foreach (DnsResourceRecord refRecord in refRecords)
            {
                switch (refRecord.Type)
                {
                    case DnsResourceRecordType.NS:
                        DnsNSRecordData nsRecord = refRecord.RDATA as DnsNSRecordData;
                        if (nsRecord is not null)
                            ResolveAdditionalRecords(refRecord, nsRecord.NameServer, serveStale, additionalRecords);

                        break;

                    case DnsResourceRecordType.MX:
                        DnsMXRecordData mxRecord = refRecord.RDATA as DnsMXRecordData;
                        if (mxRecord is not null)
                            ResolveAdditionalRecords(refRecord, mxRecord.Exchange, serveStale, additionalRecords);

                        break;

                    case DnsResourceRecordType.SRV:
                        DnsSRVRecordData srvRecord = refRecord.RDATA as DnsSRVRecordData;
                        if (srvRecord is not null)
                            ResolveAdditionalRecords(refRecord, srvRecord.Target, serveStale, additionalRecords);

                        break;
                }
            }

            return additionalRecords;
        }

        private void ResolveAdditionalRecords(DnsResourceRecord refRecord, string domain, bool serveStale, List<DnsResourceRecord> additionalRecords)
        {
            IReadOnlyList<DnsResourceRecord> glueRecords = refRecord.GetGlueRecords();
            if (glueRecords.Count > 0)
            {
                bool added = false;

                foreach (DnsResourceRecord glueRecord in glueRecords)
                {
                    if (!glueRecord.IsStale)
                    {
                        added = true;
                        additionalRecords.Add(glueRecord);
                    }
                }

                if (added)
                    return;
            }

            if (_root.TryGet(domain, out CacheZone cacheZone))
            {
                {
                    IReadOnlyList<DnsResourceRecord> records = cacheZone.QueryRecords(DnsResourceRecordType.A, serveStale, true);
                    if ((records.Count > 0) && (records[0].Type == DnsResourceRecordType.A))
                        additionalRecords.AddRange(records);
                }

                {
                    IReadOnlyList<DnsResourceRecord> records = cacheZone.QueryRecords(DnsResourceRecordType.AAAA, serveStale, true);
                    if ((records.Count > 0) && (records[0].Type == DnsResourceRecordType.AAAA))
                        additionalRecords.AddRange(records);
                }
            }
        }

        #endregion

        #region public

        public override void RemoveExpiredRecords()
        {
            bool serveStale = _dnsServer.ServeStale;

            foreach (CacheZone zone in _root)
            {
                zone.RemoveExpiredRecords(serveStale);

                if (zone.IsEmpty)
                    _root.TryRemove(zone.Name, out _); //remove empty zone
            }
        }

        public override void Flush()
        {
            _root.Clear();
        }

        public bool DeleteZone(string domain)
        {
            return _root.TryRemove(domain, out _);
        }

        public void ListSubDomains(string domain, List<string> subDomains)
        {
            _root.ListSubDomains(domain, subDomains);
        }

        public void ListAllRecords(string domain, List<DnsResourceRecord> records)
        {
            if (_root.TryGet(domain, out CacheZone zone))
                zone.ListAllRecords(records);
        }

        public DnsDatagram QueryClosestDelegation(DnsDatagram request)
        {
            _ = _root.FindZone(request.Question[0].Name, out _, out CacheZone delegation);
            if (delegation is not null)
            {
                //return closest name servers in delegation
                IReadOnlyList<DnsResourceRecord> closestAuthority = delegation.QueryRecords(DnsResourceRecordType.NS, false, true);
                if ((closestAuthority.Count > 0) && (closestAuthority[0].Type == DnsResourceRecordType.NS) && (closestAuthority[0].Name.Length > 0)) //dont trust root name servers from cache!
                {
                    IReadOnlyList<DnsResourceRecord> additional = GetAdditionalRecords(closestAuthority, false);

                    return new DnsDatagram(request.Identifier, true, DnsOpcode.StandardQuery, false, false, request.RecursionDesired, true, false, false, DnsResponseCode.NoError, request.Question, null, closestAuthority, additional);
                }
            }

            //no cached delegation found
            return null;
        }

        public override DnsDatagram Query(DnsDatagram request, bool serveStaleAndResetExpiry = false, bool findClosestNameServers = false)
        {
            DnsQuestionRecord question = request.Question[0];

            CacheZone zone;
            CacheZone closest = null;
            CacheZone delegation = null;

            if (findClosestNameServers)
            {
                zone = _root.FindZone(question.Name, out closest, out delegation);
            }
            else
            {
                if (!_root.TryGet(question.Name, out zone))
                    _ = _root.FindZone(question.Name, out closest, out _); //zone not found; attempt to find closest
            }

            if (zone is not null)
            {
                //zone found
                IReadOnlyList<DnsResourceRecord> answers = zone.QueryRecords(question.Type, serveStaleAndResetExpiry, false);
                if (answers.Count > 0)
                {
                    //answer found in cache
                    DnsResourceRecord firstRR = answers[0];

                    if (firstRR.RDATA is DnsSpecialCacheRecord dnsSpecialCacheRecord)
                    {
                        IReadOnlyList<EDnsOption> specialOptions = null;

                        if (serveStaleAndResetExpiry)
                        {
                            if (firstRR.IsStale)
                                firstRR.ResetExpiry(30); //reset expiry by 30 seconds so that resolver tries again only after 30 seconds as per draft-ietf-dnsop-serve-stale-04

                            if (dnsSpecialCacheRecord.Authority is not null)
                            {
                                foreach (DnsResourceRecord record in dnsSpecialCacheRecord.Authority)
                                {
                                    if (record.IsStale)
                                        record.ResetExpiry(30); //reset expiry by 30 seconds so that resolver tries again only after 30 seconds as per draft-ietf-dnsop-serve-stale-04
                                }
                            }

                            if (dnsSpecialCacheRecord.RCODE == DnsResponseCode.NxDomain)
                            {
                                List<EDnsOption> newOptions = new List<EDnsOption>(dnsSpecialCacheRecord.EDnsOptions.Count + 1);

                                newOptions.AddRange(dnsSpecialCacheRecord.EDnsOptions);
                                newOptions.Add(new EDnsOption(EDnsOptionCode.EXTENDED_DNS_ERROR, new EDnsExtendedDnsErrorOption(EDnsExtendedDnsErrorCode.StaleNxDomainAnswer, null)));

                                specialOptions = newOptions;
                            }
                        }

                        if (specialOptions is null)
                            specialOptions = dnsSpecialCacheRecord.EDnsOptions;

                        if (request.DnssecOk)
                        {
                            bool authenticData;

                            switch (dnsSpecialCacheRecord.Type)
                            {
                                case DnsSpecialCacheRecordType.NegativeCache:
                                    authenticData = true;
                                    break;

                                default:
                                    authenticData = false;
                                    break;
                            }

                            if (request.CheckingDisabled)
                                return new DnsDatagram(request.Identifier, true, DnsOpcode.StandardQuery, false, false, request.RecursionDesired, true, authenticData, request.CheckingDisabled, dnsSpecialCacheRecord.OriginalRCODE, request.Question, dnsSpecialCacheRecord.OriginalAnswer, dnsSpecialCacheRecord.OriginalAuthority, dnsSpecialCacheRecord.Additional, _dnsServer.UdpPayloadSize, EDnsHeaderFlags.DNSSEC_OK, specialOptions);
                            else
                                return new DnsDatagram(request.Identifier, true, DnsOpcode.StandardQuery, false, false, request.RecursionDesired, true, authenticData, request.CheckingDisabled, dnsSpecialCacheRecord.RCODE, request.Question, null, dnsSpecialCacheRecord.Authority, null, _dnsServer.UdpPayloadSize, EDnsHeaderFlags.DNSSEC_OK, specialOptions);
                        }
                        else
                        {
                            return new DnsDatagram(request.Identifier, true, DnsOpcode.StandardQuery, false, false, request.RecursionDesired, true, false, false, dnsSpecialCacheRecord.RCODE, request.Question, null, dnsSpecialCacheRecord.NoDnssecAuthority, null, request.EDNS is null ? ushort.MinValue : _dnsServer.UdpPayloadSize, EDnsHeaderFlags.None, specialOptions);
                        }
                    }

                    DnsResourceRecord lastRR = answers[answers.Count - 1];
                    if ((lastRR.Type != question.Type) && (lastRR.Type == DnsResourceRecordType.CNAME) && (question.Type != DnsResourceRecordType.ANY))
                    {
                        List<DnsResourceRecord> newAnswers = new List<DnsResourceRecord>(answers.Count + 3);
                        newAnswers.AddRange(answers);

                        ResolveCNAME(question, lastRR, serveStaleAndResetExpiry, newAnswers);

                        answers = newAnswers;
                    }

                    IReadOnlyList<DnsResourceRecord> authority = null;
                    EDnsHeaderFlags ednsFlags = EDnsHeaderFlags.None;

                    if (request.DnssecOk)
                    {
                        //DNSSEC enabled; insert RRSIG records
                        List<DnsResourceRecord> newAnswers = new List<DnsResourceRecord>(answers.Count * 2);
                        List<DnsResourceRecord> newAuthority = null;

                        foreach (DnsResourceRecord answer in answers)
                        {
                            newAnswers.Add(answer);

                            DnsResourceRecordInfo rrInfo = answer.GetRecordInfo();

                            IReadOnlyList<DnsResourceRecord> rrsigRecords = rrInfo.RRSIGRecords;
                            if (rrsigRecords is not null)
                            {
                                newAnswers.AddRange(rrsigRecords);

                                foreach (DnsResourceRecord rrsigRecord in rrsigRecords)
                                {
                                    if (!DnsRRSIGRecordData.IsWildcard(rrsigRecord))
                                        continue;

                                    //add NSEC/NSEC3 for the wildcard proof
                                    if (newAuthority is null)
                                        newAuthority = new List<DnsResourceRecord>(2);

                                    IReadOnlyList<DnsResourceRecord> nsecRecords = rrInfo.NSECRecords;
                                    if (nsecRecords is not null)
                                    {
                                        foreach (DnsResourceRecord nsecRecord in nsecRecords)
                                        {
                                            newAuthority.Add(nsecRecord);

                                            IReadOnlyList<DnsResourceRecord> nsecRRSIGRecords = nsecRecord.GetRecordInfo().RRSIGRecords;
                                            if (nsecRRSIGRecords is not null)
                                                newAuthority.AddRange(nsecRRSIGRecords);
                                        }
                                    }
                                }
                            }
                        }

                        answers = newAnswers;
                        authority = newAuthority;
                        ednsFlags = EDnsHeaderFlags.DNSSEC_OK;
                    }

                    IReadOnlyList<DnsResourceRecord> additional = null;

                    switch (question.Type)
                    {
                        case DnsResourceRecordType.NS:
                        case DnsResourceRecordType.MX:
                        case DnsResourceRecordType.SRV:
                            additional = GetAdditionalRecords(answers, serveStaleAndResetExpiry);
                            break;
                    }

                    EDnsOption[] options = null;

                    if (serveStaleAndResetExpiry)
                    {
                        foreach (DnsResourceRecord record in answers)
                        {
                            if (record.IsStale)
                                record.ResetExpiry(30); //reset expiry by 30 seconds so that resolver tries again only after 30 seconds as per draft-ietf-dnsop-serve-stale-04
                        }

                        if (additional is not null)
                        {
                            foreach (DnsResourceRecord record in additional)
                            {
                                if (record.IsStale)
                                    record.ResetExpiry(30); //reset expiry by 30 seconds so that resolver tries again only after 30 seconds as per draft-ietf-dnsop-serve-stale-04
                            }
                        }

                        options = new EDnsOption[] { new EDnsOption(EDnsOptionCode.EXTENDED_DNS_ERROR, new EDnsExtendedDnsErrorOption(EDnsExtendedDnsErrorCode.StaleAnswer, null)) };
                    }

                    return new DnsDatagram(request.Identifier, true, DnsOpcode.StandardQuery, false, false, request.RecursionDesired, true, answers[0].DnssecStatus == DnssecStatus.Secure, request.CheckingDisabled, DnsResponseCode.NoError, request.Question, answers, authority, additional, _dnsServer.UdpPayloadSize, ednsFlags, options);
                }
            }
            else
            {
                //zone not found
                //check for DNAME in closest zone
                if (closest is not null)
                {
                    IReadOnlyList<DnsResourceRecord> answer = closest.QueryRecords(DnsResourceRecordType.DNAME, serveStaleAndResetExpiry, true);
                    if ((answer.Count > 0) && (answer[0].Type == DnsResourceRecordType.DNAME))
                    {
                        DnsResponseCode rCode;

                        if (DoDNAMESubstitution(question, answer, serveStaleAndResetExpiry, out answer))
                            rCode = DnsResponseCode.NoError;
                        else
                            rCode = DnsResponseCode.YXDomain;

                        return new DnsDatagram(request.Identifier, true, DnsOpcode.StandardQuery, false, false, request.RecursionDesired, true, answer[0].DnssecStatus == DnssecStatus.Secure, request.CheckingDisabled, rCode, request.Question, answer);
                    }
                }
            }

            //no answer in cache
            //check for closest delegation if any
            if (findClosestNameServers && (delegation is not null))
            {
                //return closest name servers in delegation
                if (question.Type == DnsResourceRecordType.DS)
                {
                    //find parent delegation
                    string domain = AuthZoneManager.GetParentZone(question.Name);
                    if (domain is null)
                        return null; //dont find NS for root

                    _ = _root.FindZone(domain, out _, out delegation);
                    if (delegation is null)
                        return null; //no cached delegation found
                }

                IReadOnlyList<DnsResourceRecord> closestAuthority = delegation.QueryRecords(DnsResourceRecordType.NS, serveStaleAndResetExpiry, true);
                if ((closestAuthority.Count > 0) && (closestAuthority[0].Type == DnsResourceRecordType.NS) && (closestAuthority[0].Name.Length > 0)) //dont trust root name servers from cache!
                {
                    if (request.DnssecOk)
                        closestAuthority = AddDSRecordsTo(delegation, serveStaleAndResetExpiry, closestAuthority);

                    IReadOnlyList<DnsResourceRecord> additional = GetAdditionalRecords(closestAuthority, serveStaleAndResetExpiry);

                    return new DnsDatagram(request.Identifier, true, DnsOpcode.StandardQuery, false, false, request.RecursionDesired, true, closestAuthority[0].DnssecStatus == DnssecStatus.Secure, request.CheckingDisabled, DnsResponseCode.NoError, request.Question, null, closestAuthority, additional);
                }
            }

            //no cached delegation found
            return null;
        }

        #endregion
    }
}
