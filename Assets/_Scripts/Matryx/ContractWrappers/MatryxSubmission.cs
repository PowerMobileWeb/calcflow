﻿using System.Collections;
using System.Collections.Generic;
using UnityEngine;

using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.ABI.Model;
using Nethereum.Contracts;
using Nethereum.JsonRpc.UnityClient;
using Nanome.Core;
using System.Numerics;
using UnityEngine.Networking;
using System.Text;

using Nanome.Maths.Serializers.JsonSerializer;
using System.Linq;
using System.Text.RegularExpressions;
using Calcflow.UserStatistics;
using static Matryx.MatryxTournament;
using System;
using Nanome.Core.Extension;

namespace Matryx
{
    public class MatryxSubmission
    {
        public MatryxSubmission()
        {
            this.dto = new SubmissionDTO();
        }
        public MatryxSubmission(string hash) : this()
        {
            this.hash = hash;
        }
        public MatryxSubmission(string title, string hash) : this(hash)
        {
            this.title = title;
        }
        public MatryxSubmission
            (
            MatryxTournament tournament, 
            string title,
            string hash = "",
            string description = null,
            string commitContent = null,
            int value = 1
            ) : this(title, hash)
        {
            this.tournament = tournament;
            dto.TournamentAddress = tournament.address;
            this.description = description;
            this.dto.Content = ""; // "QmTDNWPTf6nM5sAwqKN1unTqvRDhr5sDxDEkLRMxbwAokz";
            commit = new MatryxCommit(commitContent, value);
        }

        public string title = "";
        public string description;
        public string hash;
        public MatryxTournament tournament;
        public SubmissionDTO dto;
        public MatryxCommit commit;

        public bool calcflowCompatible = true;

        [FunctionOutput]
        public class SubmissionOutputDTO : IFunctionOutputDTO
        {
            [Parameter("tuple", 1)]
            public virtual SubmissionDTO outSubmission { get; set; }
        }

        [FunctionOutput]
        public class SubmissionDTO : IFunctionOutputDTO
        {
            [Parameter("address", "tournament", 1)]
            public string TournamentAddress { get; set; }
            [Parameter("uint256", "roundIndex", 2)]
            public BigInteger RoundIndex { get; set; }
            [Parameter("bytes32", "commitHash", 3)]
            public byte[] CommitHash { get; set; }
            [Parameter("string", "content", 4)]
            public string Content { get; set; }
            [Parameter("uint256", "reward", 5)]
            public BigInteger Reward { get; set; }
            [Parameter("uint256", "timestamp", 6)]
            public BigInteger Timestamp { get; set; }
        }

        [FunctionOutput]
        public class DetailsUpdates : IFunctionOutputDTO
        {
            [Parameter("bytes32[3]", "title")]
            public string Title { get; set; }
            [Parameter("bytes32[2]", "descHash")]
            public string[] DescHash { get; set; }
            [Parameter("bytes32[2]", "fileHash")]
            public string[] FileHash { get; set; }
        }

        public IEnumerator get(Async.EventDelegate onSuccess, Async.EventDelegate onError = null)
        {
            var url = MatryxCortex.submissionURL+hash;
            using (var submissionWWW = new WWW(url))
            {
                yield return submissionWWW;
                var res = MatryxCortex.serializer.Deserialize<object>(submissionWWW.bytes) as Dictionary<string, object>;
                var data = res["data"] as Dictionary<string, object>;
                var submission = data["submission"] as Dictionary<string, object>;

                this.title = submission["title"] as string;
                this.description = submission["description"] as string;
                this.hash = submission["hash"] as string;
                this.tournament.address = submission["tournament"] as string;
                this.commit.hash = (submission["commit"] as Dictionary<string, object>)["hash"] as string;
                var ipfsHash = (submission["commit"] as Dictionary<string, object>)["ipfsContent"] as string;
                this.commit.ipfsContentHash = ipfsHash;
                this.dto.TournamentAddress = submission["tournament"] as string;
                this.dto.RoundIndex = BigInteger.Parse(submission["roundIndex"].ToString());
                this.dto.CommitHash = Utils.HexStringToByteArray(this.commit.hash);
                this.dto.Content = submission["ipfsContent"] as string;
                this.dto.Reward = BigInteger.Parse(submission["reward"].ToString());
                this.dto.Timestamp = BigInteger.Parse(submission["timestamp"].ToString());

                bool link = false;
                var ipfsURL = "https://ipfs.infura.io:5001/api/v0";
                var ipfsObjURL = ipfsURL + "/object/get?arg=";
                var ipfsCatURL = ipfsURL + "/cat?arg=";
                using (WWW ipfsWWW = new WWW(ipfsObjURL + ipfsHash))
                {
                    yield return ipfsWWW;
                    var ipfsObj = MatryxCortex.serializer.Deserialize<object>(ipfsWWW.bytes) as Dictionary<string, object>;
                    var links = ipfsObj["Links"] as List<object>;
                    var indexOfContent = links.IndexOfElementWithValue("jsonContent.json");
                    if (indexOfContent != -1)
                    {
                        link = true;
                        var contentLink = links[indexOfContent] as Dictionary<string, object>;
                        commit.ipfsContentHash = contentLink["Hash"] as string;
                    }
                    
                    // TODO: Implement previews in a good way
                    //var secondLink = links[1] as Dictionary<string, object>;
                    //commit.previewImageHash = secondLink["Hash"] as string;
                }

                var catUrl = link ? ipfsCatURL + commit.ipfsContentHash : ipfsCatURL + ipfsHash;
                using (WWW ipfsWWW2 = new WWW(catUrl))
                {
                    yield return ipfsWWW2;
                    try
                    {
                        commit.content = Utils.Substring(ipfsWWW2.text, '{', '}');
                    }
                    catch (System.Exception e)
                    {
                        // TODO: Have fun with this
                        commit.content = "";
                        onError?.Invoke(null);
                    }
                }
                
                var ESSRegEx = "{.*rangeKeys.*rangePairs.*ExpressionKeys.*ExpressionValues.*}";
                calcflowCompatible = Regex.IsMatch(commit.content, ESSRegEx);

                onSuccess(this);
            }
        }

        public IEnumerator uploadContent()
        {
            if (dto.Content == null || dto.Content.Equals(string.Empty))
            {
                if (description != null && !description.Equals(string.Empty))
                {
                    if(commit.ipfsContentHash != null && commit.ipfsContentHash.Substring(0, 2) == "Qm")
                    {
                        var uploadToIPFS = new Utils.CoroutineWithData<string>(MatryxCortex.Instance, MatryxCortex.uploadJson(title, description, commit.ipfsContentHash));
                        yield return uploadToIPFS;
                        dto.Content = uploadToIPFS.result;
                    }
                }
            }
        }

        public IEnumerator submit(Async.EventDelegate callback=null)
        {
            StatisticsTracking.StartEvent("Matryx", "Submission Creation");

            ResultsMenu.transactionObject = this;
            var isEntrant = new Utils.CoroutineWithData<EthereumTypes.Bool>(MatryxCortex.Instance, tournament.isEntrant(NetworkSettings.activeAccount));
            yield return isEntrant;

            var tournamentInfo = new Utils.CoroutineWithData<TournamentInfo>(MatryxCortex.Instance, tournament.getInfo());
            yield return tournamentInfo;

            if (tournament.owner.Equals(NetworkSettings.activeAccount, System.StringComparison.CurrentCultureIgnoreCase))
            {
                ResultsMenu.Instance.PostFailure(this, "You own this tournament; Unable to create submission.");
                yield break;
            }

            if(!isEntrant.result.Value)
            {
                var allowance = new Utils.CoroutineWithData<BigInteger>(MatryxCortex.Instance, MatryxToken.allowance(NetworkSettings.activeAccount, MatryxPlatform.address));
                yield return allowance;
                var balance = new Utils.CoroutineWithData<BigInteger>(MatryxCortex.Instance, MatryxToken.balanceOf(NetworkSettings.activeAccount));
                yield return balance;

                Debug.Log(tournament.entryFee);
                if(balance.result < tournament.entryFee)
                {
                    ResultsMenu.Instance.SetStatus("Insufficient MTX. Please visit <link=https://app.matryx.ai/><u>our Matryx Dapp</u></link> for MTX Tokens.", true);
                    yield break;
                }

                if (allowance.result < tournament.entryFee)
                {
                    ResultsMenu.Instance.SetStatus("Approving entry fee...");

                    if (allowance.result != BigInteger.Zero)
                    {
                        var approveZero = new Utils.CoroutineWithData<bool>(MatryxCortex.Instance, MatryxToken.approve(MatryxPlatform.address, BigInteger.Zero));
                        yield return approveZero;

                        if (!approveZero.result)
                        {
                            Debug.Log("Failed to reset tournament's allowance to zero for this user. Please check the allowance this user has granted the tournament");
                            ResultsMenu.Instance.PostFailure(this);
                            yield break;
                        }
                    }

                    var approveEntryFee = new Utils.CoroutineWithData<bool>(MatryxCortex.Instance, MatryxToken.approve(MatryxPlatform.address, tournament.entryFee));
                    yield return approveEntryFee;

                    if (!approveEntryFee.result)
                    {
                        Debug.Log("Failed to set the tournament's allowance from this user to the tournament's entry fee");
                        ResultsMenu.Instance.PostFailure(this);
                        yield break;
                    }
                }

                ResultsMenu.Instance.SetStatus("Entering Tournament...");

                var enterTournament = new Utils.CoroutineWithData<bool>(MatryxCortex.Instance, tournament.enter());
                yield return enterTournament;

                if (!enterTournament.result)
                {
                    Debug.Log("Failed to enter tournament");
                    ResultsMenu.Instance.PostFailure(this);
                    yield break;
                }
            }

            ResultsMenu.Instance.SetStatus("Claiming Commit...");
            yield return commit.claim();

            ResultsMenu.Instance.SetStatus("Creating Commit...");
            yield return commit.create();

            ResultsMenu.Instance.SetStatus("Uploading Submission...");
            yield return uploadContent();

            if(!dto.Content.Contains("Qm"))
            {
                Debug.Log("Failed to upload file to IPFS");
                yield break;
            }

            ResultsMenu.Instance.SetStatus("Creating Submission in Matryx...");
            var createSubmission = new Utils.CoroutineWithData<bool>(MatryxCortex.Instance, tournament.createSubmission(this));
            yield return createSubmission;

            if(callback != null)
            {
                callback(createSubmission.result);
            }

            if (!createSubmission.result)
            {
                Debug.Log("Failed to create submission");
                yield break;
            }
        }
    }
}