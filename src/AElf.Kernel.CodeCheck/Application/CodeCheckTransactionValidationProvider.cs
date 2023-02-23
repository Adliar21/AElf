using System;
using System.Linq;
using System.Threading;
using AElf.Kernel.Blockchain.Application;
using AElf.Kernel.CodeCheck.Infrastructure;
using AElf.Kernel.SmartContract.Application;
using AElf.Kernel.Txn.Application;
using AElf.Standards.ACS0;
using Volo.Abp.EventBus.Local;

namespace AElf.Kernel.CodeCheck.Application;

internal class CodeCheckTransactionValidationProvider : ITransactionValidationProvider
{
    private readonly ICodeCheckService _codeCheckService;
    private readonly IContractReaderFactory<ACS0Container.ACS0Stub> _contractReaderFactory;
    private readonly ISmartContractAddressService _smartContractAddressService;
    private readonly IContractPatcher _contractPatcher;

    public CodeCheckTransactionValidationProvider(
        ICodeCheckService codeCheckService, IContractReaderFactory<ACS0Container.ACS0Stub> contractReaderFactory,
        ISmartContractAddressService smartContractAddressService, IContractPatcher contractPatcher)
    {
        _codeCheckService = codeCheckService;
        _contractReaderFactory = contractReaderFactory;
        _smartContractAddressService = smartContractAddressService;
        _contractPatcher = contractPatcher;
        LocalEventBus = NullLocalEventBus.Instance;
    }

    public ILocalEventBus LocalEventBus { get; set; }

    public bool ValidateWhileSyncing { get; } = false;

    public async Task<bool> ValidateTransactionAsync(Transaction transaction, IChainContext chainContext)
    {
        var executionValidationResult = true;
        switch (transaction.MethodName)
        {
            case nameof(ACS0Container.ACS0Stub.DeployUserSmartContract):
                var deployInput = ContractDeploymentInput.Parser.ParseFrom(transaction.Params);
                var patchedCode = _contractPatcher.Patch(deployInput.Code.ToByteArray(), false);
                executionValidationResult = await _codeCheckService.PerformCodeCheckAsync(patchedCode, chainContext.BlockHash,
                    chainContext.BlockHeight, deployInput.Category, false, true);
                break;
            case nameof(ACS0Container.ACS0Stub.UpdateUserSmartContract):
                var updateInput = ContractUpdateInput.Parser.ParseFrom(transaction.Params);
                var genesisContractAddress = _smartContractAddressService.GetZeroSmartContractAddress();
                var contractInfo = await _contractReaderFactory.Create(new ContractReaderContext
                {
                    BlockHash = chainContext.BlockHash,
                    BlockHeight = chainContext.BlockHeight,
                    ContractAddress = genesisContractAddress
                }).GetContractInfo.CallAsync(updateInput.Address);
                
                if (contractInfo == null || contractInfo.Author == null)
                {
                    executionValidationResult = false;
                }
                else
                {
                    var patchedUpdateCode = _contractPatcher.Patch(updateInput.Code.ToByteArray(), false);
                    executionValidationResult = await _codeCheckService.PerformCodeCheckAsync(patchedUpdateCode, chainContext.BlockHash,
                        chainContext.BlockHeight, contractInfo.Category, false, true);
                }
                break;
        }

        if (!executionValidationResult)
        {
            var transactionId = transaction.GetHash();
            await LocalEventBus.PublishAsync(new TransactionValidationStatusChangedEvent
            {
                TransactionId = transactionId,
                TransactionResultStatus = TransactionResultStatus.NodeValidationFailed,
                Error = "Contract code check failed."
            });
        }

        return executionValidationResult;
    }
}